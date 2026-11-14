import { getToken } from '../../api.js';
import { filterPasses } from '../../../lib/studio-math.js';
import { gateLevel } from '../../../lib/trance-gate.mjs';
import { barBeats } from '../../../lib/meter.mjs';
import { syncSamples } from '../../../lib/clientside-delay.mjs';
import { tempoMap } from '../../../lib/audio-clock.mjs';
import { createClockGate, duckStepEvents, gateStepEvents } from '../../../lib/live-clock-gate.mjs';
import { takeCache } from '../../../lib/take-cache.mjs';
import { whyNot } from '../../../lib/caps.mjs';
import { panGains, linearMix, additiveMix, softShape } from '../../../lib/mix-laws.mjs';
import { triangleWave, phaserWave, filterWave, tremoloWave, fillWave } from '../../../lib/live-lfo.mjs';

const effectTypes = new Set(['eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume', 'chorus', 'tremolo']);   // the offline approximate render (effectGraph); the live graph below knows more types
const FILTER_CYCLES = { '1/1': 1, '1/2': 2, '1/4': 4, '1/8': 8, '1/16': 16, '1/32': 32, '1/4t': 6, '1/8t': 12, '1/16t': 24 };   // oscillator cycles per bar of the filter LFO (as clientside-filter.mjs)
/** Where the playing take is, for the step-pattern effects (the player's media element). */
const playerClock = () => { const state = window.AMPLE_PLAYER?.state?.() || {}; return { position: state.time || 0, duration: state.duration || 0, playing: Boolean(state.playing) }; };

const CHORUS_BASE_MS = 20;   // same fixed base delay as the server's ModDelay(x, rate, 20, depth, period, 0, mix)
const number = (params, name, fallback) => {
  if (/^\s*(auto|lfo|env|fadein|fadeout)\(/.test(String(params[name] ?? ''))) return fallback;   // an expression is not a number: stripping its letters made auto(0s:0, 238s:0) read as 2380 (+48 dB)
  const value = Number.parseFloat(String(params[name] ?? '').replace(/[^+\-\d.e]/gi, ''));
  return Number.isFinite(value) ? value : fallback;
};
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
/** One band of the parametric EQ as a Web Audio biquad; a band at 0 dB (not a notch) is a flat all-pass, so it changes nothing. */
const peqBand = (params, n) => {
  const type = params[`band${n}_type`] || (n === 1 ? 'lowshelf' : n === 6 ? 'highshelf' : 'peak'), gain = clamp(number(params, `band${n}_gain`, 0), -24, 24);
  const hz = clamp(number(params, `band${n}_hz`, [80, 250, 800, 2500, 6000, 12000][n - 1]), 20, 20000), q = clamp(number(params, `band${n}_q`, 1), 0.1, 18);
  if (String(params[`band${n}_enabled`] ?? 'on').trim() === 'off') return { type: 'allpass', hz, q, gain: 0 };
  // Web Audio takes the Q of a low-pass or high-pass in dB, not as a plain factor: convert so Q means the same as on the server
  if (type === 'lowcut' || type === 'highcut') return { type: type === 'lowcut' ? 'highpass' : 'lowpass', hz, q: 20 * Math.log10(q), gain: 0 };
  if (type !== 'notch' && gain === 0) return { type: 'allpass', hz, q, gain: 0 };
  return { type: type === 'peak' ? 'peaking' : type, hz, q, gain: type === 'notch' ? 0 : gain };
};

// AudioWorklet blocks (the exact reverb): the module is loaded once per audio context; a graph built before it is ready falls back to the Web Audio nodes.
const worklets = new WeakMap();
/** Loads the worklets of the live effects into an audio context (resolves true when they are ready; callers await it before building a chain). */
export function prepareWorklets(context) {
  if (!context?.audioWorklet) return Promise.resolve(false);
  if (!worklets.has(context)) {
    const entry = { ready: false };
    entry.promise = Promise.all(['/lib/reverb-worklet.mjs', '/lib/dynamics-worklet.mjs', '/lib/distortion-worklet.mjs', '/lib/lufs-worklet.mjs'].map((url) => context.audioWorklet.addModule(url))).then(() => { entry.ready = true; return true; }).catch(() => false);
    worklets.set(context, entry);
  }
  return worklets.get(context).promise;
}
const workletReady = (context) => worklets.get(context)?.ready === true;
const WORKLET_DYNAMICS = new Set(['gate', 'limiter', 'deesser']);
/** The limiter parameters of the master block, as the build preview (clientside-mix.mjs masterAudioBuffer) gives them: the ceiling is the `limiter` value in whole dB, never below -12. */
export const masterLimiter = (params) => { const v = Number.parseInt(String(params.limiter ?? '').replace(/dB/gi, '').trim(), 10); return { ceiling: `${Math.max(-12, Number.isFinite(v) ? Math.max(-60, Math.min(48, v)) : -1)} dB`, release: '100', lookahead: '5' }; };

/**
 * An LFO for a live graph. Without a clock it is an OscillatorNode (free-running, as before). With a clock { position(), loop(), tracked } it is a looping buffer of the exact wave of the
 * render (live-lfo.mjs) that starts at the loop position, so the sweep has the phase it has in the render and restarts with the loop. set({ hz, type, key, wave }): hz and the oscillator type
 * serve the free-running one; key names the wave (nothing is rebuilt while it is the same) and wave() makes the value function of the sample index.
 */
function lfoNode(context, clock) {
  const out = context.createGain();
  let osc = null, buffered = null, key = '';
  if (!clock) { osc = context.createOscillator(); osc.connect(out); osc.start(); }
  const stopBuffered = () => { if (!buffered) return; clock.tracked?.delete(buffered); try { buffered.stop(); } catch { /* not started */ } buffered.disconnect(); buffered = null; };
  return {
    out,
    connect: (destination) => out.connect(destination),
    set({ hz, type, key: next, wave }) {
      if (osc) { osc.type = type; osc.frequency.cancelScheduledValues(context.currentTime); osc.frequency.setTargetAtTime(hz, context.currentTime, .01); return; }
      if (next === key) return;
      key = next;
      const loop = Math.max(.05, clock.loop() || 1), length = Math.max(2, Math.round(loop * context.sampleRate));
      const buffer = context.createBuffer(1, length, context.sampleRate);
      fillWave(buffer.getChannelData(0), wave());
      stopBuffered();
      buffered = context.createBufferSource(); buffered.buffer = buffer; buffered.loop = true; buffered.connect(out);
      buffered.start(context.currentTime, ((clock.position() % buffer.duration) + buffer.duration) % buffer.duration);
      clock.tracked?.add(buffered);
    },
    dispose() { try { osc?.stop(); } catch { /* not started */ } stopBuffered(); out.disconnect(); },
  };
}

// Persistent nodes: changing a knob never replaces or restarts the playing sample. `clock` ({ position(), loop(), tracked }) lets the LFOs follow the loop of the chain (see lfoNode).
export function liveEffectGraph(context, source, output, type, initial, tempo, impulse, clock = null) {
  const nodes = [], node = (value) => { nodes.push(value); return value; };
  const dry = node(context.createGain()), wet = node(context.createGain()), input = node(context.createGain());
  source.connect(dry).connect(output); source.connect(input); wet.connect(output);
  const set = (param, value) => { param.cancelScheduledValues(context.currentTime); param.setTargetAtTime(value, context.currentTime, .01); };
  let suspendHook = null, updateEffect, updateMix = () => {}, replaceImpulse = null, oscillators = [], latency = 0, keyInput = null, setKey = null, loudness = null;   // latency in seconds (a limiter looks ahead)
  const disposers = [];
  if (type === 'volume') {
    const splitter = node(context.createChannelSplitter(2)), merger = node(context.createChannelMerger(2));
    input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(splitter);
    const paths = Array.from({ length: 4 }, () => node(context.createGain()));
    paths.forEach((gain, index) => { splitter.connect(gain, Math.floor(index / 2)); gain.connect(merger, 0, index % 2); });
    merger.connect(wet);
    updateEffect = (params) => {
      const gain = 10 ** (clamp(number(params, 'gain', 0), -60, 48) / 20), mono = params.mono === 'on';
      paths.forEach((path, index) => { const sourceChannel = Math.floor(index / 2), outputChannel = index % 2, channel = outputChannel ? 'right' : 'left'; set(path.gain, params[channel] === 'off' ? 0 : gain * 10 ** (clamp(number(params, `${channel}_gain`, 0), -60, 12) / 20) * (mono ? .5 : sourceChannel === outputChannel ? 1 : 0)); });
    };
  } else if (type === 'eq') {
    const low = node(context.createBiquadFilter()), mid = node(context.createBiquadFilter()), high = node(context.createBiquadFilter());
    low.type = 'lowshelf'; mid.type = 'peaking'; mid.Q.value = .8; high.type = 'highshelf'; input.connect(low).connect(mid).connect(high).connect(wet);
    updateEffect = (params) => {
      const lowHz = number(params, 'low_hz', 250), highHz = number(params, 'high_hz', 3000);
      set(low.frequency, lowHz); set(high.frequency, highHz); set(mid.frequency, Math.sqrt(lowHz * highHz));
      set(low.gain, number(params, 'low', 0)); set(mid.gain, number(params, 'mid', 0)); set(high.gain, number(params, 'high', 0));
    };
  } else if (type === 'peq') {
    const stages = Array.from({ length: 6 }, () => node(context.createBiquadFilter()));
    stages.reduce((from, stage) => from.connect(stage), input).connect(wet);
    updateEffect = (params) => stages.forEach((stage, index) => { const band = peqBand(params, index + 1); stage.type = band.type; set(stage.frequency, band.hz); set(stage.Q, band.q); set(stage.gain, band.gain); });
  } else if (type === 'filter') {
    // four filters in a row: slope 24 is two of them, 36 three, 48 all four; an unused one is a flat all-pass (no change)
    const stages = Array.from({ length: 4 }, () => node(context.createBiquadFilter()));
    stages.reduce((from, stage) => from.connect(stage), input).connect(wet);
    // the cutoff wobble: cutoff * 2^(depth * wave), wave 0..1, as cents on every stage (an oscillator of -1..1 times depth*600 cents on top of depth*600 cents)
    const lfo = lfoNode(context, clock), lfoGain = node(context.createGain());
    lfo.connect(lfoGain); stages.forEach((stage) => lfoGain.connect(stage.detune)); disposers.push(lfo.dispose);
    updateEffect = (params) => {
      const kind = ['highpass', 'bandpass', 'notch'].includes(params.type) ? params.type : 'lowpass', q = 1 + number(params, 'resonance', 0) * 0.17, passes = filterPasses(params.slope);
      const cycles = FILTER_CYCLES[params.lfo_rate ?? params.lfo_sync] || 0, depth = cycles ? clamp(number(params, 'lfo_depth', 0), 0, 8) : 0;
      const shape = params.lfo_shape === 'saw' ? 'saw' : ['triangle', 'square'].includes(params.lfo_shape) ? params.lfo_shape : 'sine', bpm = Math.max(1, Number(tempo) || 120), barSamples = Math.floor(barBeats() * 60 * context.sampleRate / bpm);
      lfo.set({ hz: Math.max(.01, cycles * bpm / (barBeats() * 60)), type: shape === 'saw' ? 'sawtooth' : shape, key: `filter:${shape}:${cycles}:${barSamples}`, wave: () => filterWave(shape, cycles, barSamples) });
      set(lfoGain.gain, depth * 600);
      stages.forEach((stage, index) => { stage.type = index < passes ? kind : 'allpass'; set(stage.frequency, number(params, 'cutoff', 1000)); set(stage.Q, q); set(stage.detune, depth * 600); });
    };
  } else if (type === 'compressor' && workletReady(context)) {
    // the compressor of the render (CompressorCore) in an AudioWorklet; its second input is the sidechain key (the chain connects a take to keyInput and calls setKey(true))
    const options = { numberOfInputs: 2, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit', processorOptions: { kind: 'compressor', params: initial } };
    const compressor = node(new AudioWorkletNode(context, 'ample-dynamics', options)), key = node(context.createGain());
    input.connect(compressor).connect(wet); key.connect(compressor, 0, 1);
    keyInput = key; setKey = (on) => compressor.port.postMessage({ key: Boolean(on) });
    updateEffect = (params) => compressor.port.postMessage({ params });
  } else if (type === 'compressor') {
    const compressor = node(context.createDynamicsCompressor()), makeup = node(context.createGain()); input.connect(compressor).connect(makeup).connect(wet);
    updateEffect = (params) => {
      set(compressor.threshold, number(params, 'threshold', -18)); set(compressor.ratio, clamp(number(params, 'ratio', 4), 1, 20));
      set(compressor.attack, number(params, 'attack', 10) / 1000); set(compressor.release, number(params, 'release', 120) / 1000); set(makeup.gain, 10 ** (number(params, 'makeup', 0) / 20));
    };
  } else if (type === 'duck') {
    // the dips are queued on the audio clock a few hundred ms ahead (live-clock-gate.mjs), so the pumping does not jitter when the page is busy
    const gain = node(context.createGain()); input.connect(gain).connect(wet);
    let params = initial;
    const gate = createClockGate(context, gain.gain, {
      clock: playerClock, stepSeconds: () => 60 / Math.max(1, Number(tempo) || 120) / 4, map: tempoMap([{ beat: 0, bpm: Math.max(1, Number(tempo) || 120) }]), stepBeats: .25,
      stepEvents: (k, step) => duckStepEvents(params.pattern, k, step, number(params, 'depth', 10), number(params, 'release', 160) / 1000),
    });
    updateEffect = (values) => { params = values; gate.reset(); gate.tick(); };
    disposers.push(gate.dispose);
  } else if (type === 'tremolo') {
    // shape gate: the gain follows the step pattern from the playhead, queued on the audio clock; the other shapes are an LFO on the gain
    const gain = node(context.createGain()), lfo = lfoNode(context, clock), lfoGain = node(context.createGain());
    lfo.connect(lfoGain); lfoGain.connect(gain.gain); disposers.push(lfo.dispose);
    const gateGain = node(context.createGain());   // the gate has its own gain so the LFO and the step pattern never fight over one param
    input.connect(gateGain).connect(gain).connect(wet);
    let params = initial;
    const gate = createClockGate(context, gateGain.gain, {
      clock: () => (params.shape === 'gate' ? playerClock() : { playing: false }),
      stepSeconds: () => 60 / Math.max(1, Number(tempo) || 120) * barBeats() / liveStepDivision(params),
      map: tempoMap([{ beat: 0, bpm: Math.max(1, Number(tempo) || 120) }]), stepBeats: () => barBeats() / liveStepDivision(params),
      stepEvents: (k, step) => gateStepEvents(params.pattern, k, step, clamp(number(params, 'depth', 50), 0, 100) / 100),
    });
    updateEffect = (values) => {
      params = values;
      const isGate = values.shape === 'gate', depth = clamp(number(values, 'depth', 50), 0, 100) / 100, division = liveStepDivision(values);
      const hz = values.sync && values.sync !== 'off' ? division * Math.max(1, Number(tempo) || 120) / (barBeats() * 60) : clamp(number(values, 'rate', 4), .05, 40);
      const shape = values.shape === 'square' ? 'square' : values.shape === 'triangle' ? 'triangle' : 'sine', phase = number(values, 'phase', 0);
      lfo.set({ hz, type: shape, key: `trem:${shape}:${hz}:${phase}`, wave: () => tremoloWave(shape, hz, context.sampleRate, phase) });
      set(lfoGain.gain, isGate ? 0 : depth / 2);
      set(gain.gain, isGate ? 1 : 1 - depth / 2);
      gate.reset(); if (isGate) gate.tick(); else { gateGain.gain.cancelScheduledValues(context.currentTime); gateGain.gain.setValueAtTime(1, context.currentTime); }
    };
    disposers.push(gate.dispose);
  } else if (type === 'pan') {
    // the balance law of the offline preview (clientside-pan.mjs): the far side gets quieter, the near side is left alone
    const splitter = node(context.createChannelSplitter(2)), merger = node(context.createChannelMerger(2)), left = node(context.createGain()), right = node(context.createGain());
    input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(splitter);
    splitter.connect(left, 0); splitter.connect(right, 1); left.connect(merger, 0, 0); right.connect(merger, 0, 1); merger.connect(wet);
    updateEffect = (params) => {
      const gains = panGains(clamp(Math.trunc(number(params, 'pan', 0)), -32, 32), true);
      set(left.gain, gains.left); set(right.gain, gains.right);
    };
  } else if (type === 'width') {
    // mid/side as a 2x2 matrix: L' = aL + bR, R' = bL + aR with a = 1/2 + w/200 and b = 1/2 - w/200 (100 keeps the image, 0 is mono, 200 doubles the side)
    const splitter = node(context.createChannelSplitter(2)), merger = node(context.createChannelMerger(2));
    input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(splitter);
    const paths = Array.from({ length: 4 }, () => node(context.createGain()));
    paths.forEach((gain, index) => { splitter.connect(gain, Math.floor(index / 2)); gain.connect(merger, 0, index % 2); });
    updateEffect = (params) => {
      const width = clamp(Math.trunc(number(params, 'width', 100)), 0, 200), a = .5 + width / 200, b = .5 - width / 200;
      paths.forEach((gain, index) => set(gain.gain, Math.floor(index / 2) === index % 2 ? a : b));
    };
    merger.connect(wet);
  } else if (type === 'phaser') {
    // all-pass stages swept by a triangle: two first-order stages of the server make one biquad all-pass (Q 0.5); the sweep curve is the server's coefficient law as a frequency
    const count = 4, stages = Array.from({ length: count }, () => node(context.createBiquadFilter())), lfo = lfoNode(context, clock), shaper = node(context.createWaveShaper());
    const rate = context.sampleRate, curve = new Float32Array(1025);
    for (let index = 0; index < curve.length; index++) { const coefficient = (3277 + (index / 1024) * 26000) / 32768; curve[index] = 2 * Math.atan((1 + coefficient) / (1 - coefficient)) / (2 * Math.PI) * rate; }
    lfo.connect(shaper);   // the LFO's -1..1 is the shaper's input range, so index 0..1024 is the triangle position 0..1
    shaper.curve = curve; disposers.push(lfo.dispose);
    stages.forEach((stage) => { stage.type = 'allpass'; stage.Q.value = .5; stage.frequency.value = 0; shaper.connect(stage.frequency); });
    let used = 0;
    updateEffect = (params) => {
      const next = Math.ceil(clamp(Math.trunc(number(params, 'stages', 4)), 2, 8) / 2);
      if (next !== used) {   // only the stages in use are in the chain (an unused all-pass would still shift phase)
        input.disconnect(); stages.forEach((stage) => stage.disconnect());
        input.connect(stages[0]); stages.slice(0, next - 1).forEach((stage, index) => stage.connect(stages[index + 1])); stages[next - 1].connect(wet); used = next;
      }
      const periodMs = clamp(number(params, 'period', 2500), 100, 10000), periodSamples = Math.max(2, Math.floor(periodMs * rate / 1000));
      lfo.set({ hz: 1000 / periodMs, type: 'triangle', key: `phaser:${periodSamples}`, wave: () => phaserWave(periodSamples) });
    };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 50), 0, 100) / 100; set(dry.gain, 1 - amount); set(wet.gain, amount); };
  } else if (type === 'flanger') {
    // a short delay (3 ms base, depth 1 to 3 ms swept by a triangle) with feedback; a loop in Web Audio cannot be shorter than 128 samples, so the sweep's lowest part is a little shorter-clamped than offline
    const delay = node(context.createDelay(.02)), feedback = node(context.createGain()), lfo = lfoNode(context, clock), lfoGain = node(context.createGain());
    lfo.connect(lfoGain); lfoGain.connect(delay.delayTime); disposers.push(lfo.dispose);
    delay.delayTime.value = .003; input.connect(delay).connect(wet); delay.connect(feedback).connect(delay);
    updateEffect = (params) => {
      const periodMs = clamp(number(params, 'period', 2000), 100, 10000), periodSamples = Math.max(2, Math.floor(periodMs * context.sampleRate / 1000));
      set(lfoGain.gain, clamp(number(params, 'depth', 2), 1, 3) / 1000);
      lfo.set({ hz: 1000 / periodMs, type: 'triangle', key: `tri:${periodSamples}`, wave: () => triangleWave(periodSamples) });
      set(feedback.gain, clamp(number(params, 'feedback', 50), 0, 90) / 100);
    };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 50), 0, 100) / 100; set(dry.gain, 1 - amount); set(wet.gain, amount); };
  } else if (type === 'distortion' && workletReady(context)) {
    // the exact distortion: the core of the offline render (distortion-core.mjs), four-point mean and mix inside, so no oversampler latency shifts the wet path
    const stage = node(new AudioWorkletNode(context, 'ample-distortion', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit', processorOptions: { params: initial } }));
    input.connect(stage).connect(wet);
    updateEffect = (params) => stage.port.postMessage({ params });
  } else if (type === 'distortion') {
    const shaper = node(context.createWaveShaper()), tone = node(context.createBiquadFilter()), level = node(context.createGain());
    shaper.oversample = '4x'; tone.type = 'lowpass'; input.connect(shaper).connect(tone).connect(level).connect(wet);
    let shapeKey = '';
    updateEffect = (params) => {
      const key = `${params.drive}:${params.mode}`;
      if (key !== shapeKey) { shaper.curve = distortionCurve(number(params, 'drive', 12), params.mode, 2049); shapeKey = key; }
      set(tone.frequency, number(params, 'tone', 8000)); set(level.gain, 10 ** (number(params, 'level', 0) / 20));
    };
    updateMix = (params) => { const gains = linearMix(number(params, 'mix', 100)); set(dry.gain, gains.dry); set(wet.gain, gains.wet); };   // the server blends dry*(1-m) + wet*m
  } else if (type === 'delay') {
    // echo[n] = x[n-L] + feedback * echo[n-L] like the server: the first echo is one delay line, the repeats go round a second one, shorter by the render quantum that Web Audio adds inside a loop
    const delay = node(context.createDelay(2)), loop = node(context.createDelay(2)), echo = node(context.createGain()), feedback = node(context.createGain()), quantum = 128 / context.sampleRate;
    input.connect(delay).connect(echo).connect(wet); echo.connect(feedback).connect(loop).connect(echo);
    updateEffect = (params) => { const time = clamp(syncSamples(params, context.sampleRate, tempo) / context.sampleRate || number(params, 'time', 250) / 1000, .001, 2); set(delay.delayTime, time); set(loop.delayTime, Math.max(quantum, time - quantum)); set(feedback.gain, clamp(number(params, 'feedback', 35) / 100, 0, .9)); };
    updateMix = (params) => { const gains = additiveMix(number(params, 'mix', 30)); set(dry.gain, gains.dry); set(wet.gain, gains.wet); };   // the server adds the echo to the untouched dry sound
  } else if (type === 'master') {
    // the end of the song: the limiter of the render (LimiterCore, ceiling as the master of the build uses it), then a loudness meter (BS.1770, lufs-core.mjs); the target is a reference
    if (!workletReady(context)) throw new Error(`The live master: ${whyNot('worklet') || 'the audio worklets are not ready yet, try again in a moment.'}`);
    const options = { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit' };
    const limiter = node(new AudioWorkletNode(context, 'ample-dynamics', { ...options, processorOptions: { kind: 'limiter', params: masterLimiter(initial) } }));
    const meter = node(new AudioWorkletNode(context, 'ample-lufs', options)), silent = node(context.createGain());
    input.connect(limiter).connect(meter).connect(wet); meter.connect(silent); silent.gain.value = 0; silent.connect(context.destination);   // a worklet without a path to the destination may not be run
    const last = { momentary: -Infinity, shortTerm: -Infinity, integrated: -Infinity };
    meter.port.onmessage = ({ data }) => Object.assign(last, data);
    loudness = { read: () => ({ ...last }), reset: () => { meter.port.postMessage({ reset: true }); Object.assign(last, { momentary: -Infinity, shortTerm: -Infinity, integrated: -Infinity }); } };
    updateEffect = (params) => limiter.port.postMessage({ params: masterLimiter(params) });
    latency = Math.max(1, Math.trunc(5 * context.sampleRate / 1000)) / context.sampleRate;
  } else if (WORKLET_DYNAMICS.has(type)) {
    // gate, limiter and de-esser run on the cores of the offline renders (dynamics-core.mjs) in an AudioWorklet; the limiter delays the sound by its look-ahead
    if (!workletReady(context)) throw new Error(`The live ${type}: ${whyNot('worklet') || 'the audio worklets are not ready yet, try again in a moment.'}`);
    const options = { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit', processorOptions: { kind: type, params: initial } };
    const dynamics = node(new AudioWorkletNode(context, 'ample-dynamics', options));
    input.connect(dynamics).connect(wet);
    updateEffect = (params) => dynamics.port.postMessage({ params });
    if (type === 'limiter') latency = Math.max(1, Math.trunc(clamp(number(initial, 'lookahead', 5), 1, 20) * context.sampleRate / 1000)) / context.sampleRate;
  } else if (type === 'reverb' && workletReady(context)) {
    // the exact room: the integer recurrence of the server (reverb-stream.mjs), also run by the offline render; the dry sound and the mix are inside it
    const room = node(new AudioWorkletNode(context, 'ample-reverb', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit', processorOptions: { size: initial.size, decay: initial.decay, mix: initial.mix } }));
    input.connect(room).connect(wet);
    updateEffect = (params) => room.port.postMessage({ size: params.size, decay: params.decay, mix: params.mix });
    let suspendTimer = 0;   // after the wet fade is over a bypassed room stops computing; switching it on wakes it at once
    suspendHook = (off) => { clearTimeout(suspendTimer); if (off) suspendTimer = setTimeout(() => room.port.postMessage({ suspended: true }), 150); else room.port.postMessage({ suspended: false }); };
    disposers.push(() => clearTimeout(suspendTimer));
  } else if (type === 'reverb') {
    updateEffect = connectReverb(context, input, wet, node);
    updateMix = (params) => { set(dry.gain, 1); set(wet.gain, clamp(number(params, 'mix', 25), 0, 100) / 100); };   // as on the server: the dry sound stays, mix is the level of the room
  } else if (type === 'chorus') {
    // a delay line modulated by a triangle LFO, matching the server's fixed 20 ms base delay with depth/period swept on top (no feedback, unlike flanger)
    // like the server on a stereo sound: the right channel sweeps a quarter of a period ahead of the left (width); one delay line per channel
    const splitter = node(context.createChannelSplitter(2)), merger = node(context.createChannelMerger(2));
    input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(splitter);
    const lanes = [0, 1].map((channel) => {
      const delay = node(context.createDelay(.05)), lfo = lfoNode(context, clock), lfoGain = node(context.createGain());
      lfo.connect(lfoGain); lfoGain.connect(delay.delayTime); disposers.push(lfo.dispose);
      splitter.connect(delay, channel); delay.connect(merger, 0, channel);
      return { delay, lfo, lfoGain, channel };
    });
    merger.connect(wet);
    updateEffect = (params) => {
      const periodMs = clamp(number(params, 'period', 1500), 100, 10000), periodSamples = Math.max(2, Math.floor(periodMs * context.sampleRate / 1000));
      for (const { delay, lfo, lfoGain, channel } of lanes) {
        delay.delayTime.value = CHORUS_BASE_MS / 1000;   // the base is fixed: no smoothing, or the first 100 ms would sweep in from 0
        set(lfoGain.gain, clamp(number(params, 'depth', 5), 1, 15) / 1000);
        const offset = channel ? Math.floor(periodSamples / 4) : 0;
        lfo.set({ hz: 1000 / periodMs, type: 'triangle', key: `tri:${periodSamples}:${offset}`, wave: () => triangleWave(periodSamples, offset) });
      }
    };
    updateMix = (params) => { const gains = linearMix(number(params, 'mix', 50)); set(dry.gain, gains.dry); set(wet.gain, gains.wet); };
  } else if (type === 'convolution') {
    if (!impulse) throw new Error('Choose an impulse response before previewing convolution.');
    // two convolvers behind a gain pair: a new impulse (length, normalize, another file) is loaded into the idle one and crossfaded in over about 30 ms,
    // so swapping the buffer of a convolver that is playing (which clicks) never happens. The old one is cleared after the fade.
    const predelay = node(context.createDelay(.5)), level = node(context.createGain());
    const convolvers = [0, 1].map(() => { const c = node(context.createConvolver()); c.normalize = false; return c; });
    const fades = [0, 1].map(() => node(context.createGain()));
    input.connect(predelay);
    convolvers.forEach((c, i) => { predelay.connect(c); c.connect(fades[i]).connect(level); });
    level.connect(wet); fades[0].gain.value = 1; fades[1].gain.value = 0;
    let impulseKey = '', built = false, currentImpulse = impulse, active = 0, lastSwap = -1, pending = null, clearTimer = null;
    const fade = (param, value) => { param.cancelScheduledValues(context.currentTime); param.setTargetAtTime(value, context.currentTime, .008); };
    const swapImpulse = (params) => {
      const idle = 1 - active, old = active;
      convolvers[idle].buffer = convolutionImpulse(context, currentImpulse, params);
      fade(fades[idle].gain, 1); fade(fades[old].gain, 0); active = idle; lastSwap = context.currentTime;
      clearTimeout(clearTimer); clearTimer = setTimeout(() => { if (active !== old) convolvers[old].buffer = null; }, 400);
    };
    disposers.push(() => { clearTimeout(clearTimer); clearTimeout(pending); });
    updateEffect = (params) => {
      const key = `${params.length}:${params.normalize}`;
      if (key !== impulseKey) {
        const first = !built;
        impulseKey = key; built = true;
        if (first) convolvers[0].buffer = convolutionImpulse(context, currentImpulse, params);
        else if (context.currentTime - lastSwap < .06 && typeof context.startRendering !== 'function') { clearTimeout(pending); pending = setTimeout(() => swapImpulse(params), 70); }   // a fast drag: only the last value is loaded, after the fade in flight has finished
        else swapImpulse(params);
      }
      set(predelay.delayTime, clamp(number(params, 'predelay', 0) / 1000, 0, .5));
      set(level.gain, 10 ** (number(params, 'level', 0) / 20));
    };
    replaceImpulse = (next, params) => { currentImpulse = next; impulseKey = ''; updateEffect(params); };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 30), 0, 100) / 100; set(dry.gain, 1 - amount); set(wet.gain, amount); };
  } else { input.connect(wet); updateEffect = () => {}; }
  const update = (params) => {
    updateEffect(params); set(dry.gain, 0); set(wet.gain, 1); updateMix(params);
    if (params.enabled === 'off') { set(dry.gain, 1); set(wet.gain, 0); }
    suspendHook?.(params.enabled === 'off');
  };
  // start on the values the first update aims at, so the dry sound is not doubled for the first 50 ms while the smoothing settles
  dry.gain.value = initial.enabled === 'off' ? 1 : 0; wet.gain.value = initial.enabled === 'off' ? 0 : 1;
  update(initial);
  // oscillators must be stopped, not just disconnected, or they keep running silently on the shared meter context across every later preview
  return { update, replaceImpulse, suspends: Boolean(suspendHook), keyInput, setKey, loudness, get latency() { return latency; }, dispose: () => { disposers.forEach((value) => value()); oscillators.forEach((value) => value.stop()); nodes.forEach((value) => value.disconnect()); } };
}

/** Steps (or cycles) per bar of the tremolo: the sync value (1/8 is 8), 16 when sync is off (a gate step is a sixteenth then). */
export function liveStepDivision(params) {
  const division = Number.parseInt(String(params.sync || '').split('/')[1], 10);
  return Number.isFinite(division) && division > 0 ? division : 16;
}

export function convolutionImpulse(context, input, params) {
  const channels = input.numberOfChannels, ratio = input.sampleRate / context.sampleRate;
  const length = Math.max(1, Math.min(Math.floor(clamp(number(params, 'length', 3), .05, 20) * context.sampleRate), Math.ceil(input.length / ratio)));
  const output = context.createBuffer(1, length, context.sampleRate), samples = output.getChannelData(0);
  const data = Array.from({ length: channels }, (_, index) => input.getChannelData(index));
  let energy = 0;
  for (let index = 0; index < length; index++) {
    const position = index * ratio, first = Math.min(input.length - 1, Math.floor(position)), next = Math.min(input.length - 1, first + 1), fraction = position - first;
    for (const channel of data) samples[index] += (channel[first] * (1 - fraction) + channel[next] * fraction) / channels;
    energy += samples[index] * samples[index];
  }
  if (params.normalize !== 'off' && energy > 0) {
    const scale = 1 / Math.sqrt(energy);
    for (let index = 0; index < length; index++) samples[index] *= scale;
  }
  return output;
}

export function distortionCurve(driveDb, mode, length) {
  const curve = new Float32Array(length), drive = 10 ** (clamp(driveDb, 0, 36) / 20);
  for (let index = 0; index < length; index++) {
    const input = (index * 2) / (length - 1) - 1, driven = input * drive;
    curve[index] = mode === 'hard' ? clamp(driven, -1, 1) : softShape(driven);
  }
  return curve;
}

export function duckAutomationEvents(pattern, depthDb, releaseMs, duration, tempo) {
  const stepDuration = 60 / Math.max(1, tempo) / 4, floor = 10 ** (-clamp(depthDb, 0, 40) / 20), events = [];
  const steps = String(pattern || 'x...x...x...x...').padEnd(16, '.').slice(0, 16);
  for (let start = 0; start < duration; start += stepDuration) {
    if (steps[Math.floor(start / stepDuration) % 16] !== 'x') continue;
    const release = Math.min(releaseMs / 1000, duration - start);
    events.push({ start, end: start + release, floor });
  }
  return events;
}

// the room of the server (Effects.cs Reverb): the lows out first, eight damped combs, four all-passes (Freeverb lengths; the right channel a little longer)
const REVERB_COMBS = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], REVERB_ALLPASSES = [556, 441, 341, 225], REVERB_SPREAD = 23;
function connectReverb(context, input, output, node) {
  const splitter = node(context.createChannelSplitter(2)), merger = node(context.createChannelMerger(2)), rate = context.sampleRate;
  const lowCut = node(context.createBiquadFilter());
  lowCut.type = 'highpass'; lowCut.frequency.value = 160; lowCut.Q.value = .5;
  input.channelCount = 2; input.channelCountMode = 'explicit'; input.connect(lowCut).connect(splitter);
  const combs = [], allpasses = [], feeds = [];
  for (let channel = 0; channel < 2; channel++) {
    const sum = node(context.createGain()), feed = node(context.createGain()), spread = channel ? REVERB_SPREAD : 0;
    splitter.connect(feed, channel); feeds.push(feed);
    for (const base of REVERB_COMBS) {
      // the damping (the highs die sooner than the lows) is a gentle low pass inside the loop
      const delay = node(context.createDelay(1)), damp = node(context.createBiquadFilter()), feedback = node(context.createGain());
      damp.type = 'lowpass'; damp.frequency.value = Math.min(rate * .19, rate / 2 - 1); damp.Q.value = .5;
      feed.connect(delay); delay.connect(damp).connect(feedback).connect(delay); delay.connect(sum);
      combs.push({ delay, feedback, samples: base + spread });
    }
    let signal = sum;
    for (const base of REVERB_ALLPASSES) {
      // out = delayed - in; the line takes in + delayed / 2
      const delay = node(context.createDelay(1)), invert = node(context.createGain()), half = node(context.createGain()), stage = node(context.createGain());
      invert.gain.value = -1; half.gain.value = .5;
      signal.connect(invert).connect(stage); signal.connect(delay); delay.connect(stage); delay.connect(half).connect(delay);
      allpasses.push({ delay, samples: base + spread }); signal = stage;
    }
    signal.connect(merger, 0, channel);
  }
  merger.connect(output);
  return (params) => {
    const size = clamp(number(params, 'size', 50), 1, 100), decay = clamp(number(params, 'decay', 60), 0, 95);
    const g = .7 + .28 * decay / 95, level = Math.sqrt(.5 * (1 - g * g) / REVERB_COMBS.length);
    for (const feed of feeds) feed.gain.value = level;
    for (const comb of combs) {
      comb.delay.delayTime.value = Math.max(1, Math.floor(comb.samples * rate / 44100 * (60 + size) / 110)) / rate;
      comb.feedback.gain.value = g;
    }
    for (const allpass of allpasses) allpass.delay.delayTime.value = Math.max(1, Math.floor(allpass.samples * rate / 44100)) / rate;
  };
}

/** The blend follows the server law of the block: 'linear' (dry*(1-m) + wet*m) or 'additive' (the dry sound stays, the effect is added: delay). */
function wetDry(context, input, effect, output, mix, law = 'linear') {
  const dry = context.createGain(), wet = context.createGain(), gains = (law === 'additive' ? additiveMix : linearMix)(mix);
  dry.gain.value = gains.dry;
  wet.gain.value = gains.wet;
  input.connect(dry).connect(output);
  effect.connect(wet).connect(output);   // `effect` is the exit of the processed path; the raw input must not reach it (it would be heard in the wet part as well)
}

export function effectGraph(context, source, output, type, params, tempo, duration) {
  if (!effectTypes.has(type) || params.enabled === 'off') { source.connect(output); return; }
  if (type === 'volume') { liveEffectGraph(context, source, output, type, params, tempo); return; }
  if (type === 'eq') {
    const low = context.createBiquadFilter(), mid = context.createBiquadFilter(), high = context.createBiquadFilter();
    low.type = 'lowshelf'; low.frequency.value = number(params, 'low_hz', 250); low.gain.value = number(params, 'low', 0);
    mid.type = 'peaking'; mid.frequency.value = Math.sqrt(number(params, 'low_hz', 250) * number(params, 'high_hz', 3000)); mid.Q.value = .8; mid.gain.value = number(params, 'mid', 0);
    high.type = 'highshelf'; high.frequency.value = number(params, 'high_hz', 3000); high.gain.value = number(params, 'high', 0);
    source.connect(low).connect(mid).connect(high).connect(output); return;
  }
  if (type === 'peq') {
    let chain = source;
    for (let n = 1; n <= 6; n++) {
      const band = peqBand(params, n), stage = context.createBiquadFilter();
      stage.type = band.type; stage.frequency.value = band.hz; stage.Q.value = band.q; stage.gain.value = band.gain;
      chain.connect(stage); chain = stage;
    }
    chain.connect(output); return;
  }
  if (type === 'filter') {
    const kind = ['highpass', 'bandpass', 'notch'].includes(params.type) ? params.type : 'lowpass';
    let chain = source;
    for (let pass = 0; pass < filterPasses(params.slope); pass++) {
      const filter = context.createBiquadFilter(); filter.type = kind; filter.frequency.value = number(params, 'cutoff', 1000); filter.Q.value = 1 + number(params, 'resonance', 0) * 0.17;
      chain.connect(filter); chain = filter;
    }
    chain.connect(output); return;
  }
  if (type === 'compressor') {
    const compressor = context.createDynamicsCompressor(), makeup = context.createGain();
    compressor.threshold.value = number(params, 'threshold', -18); compressor.ratio.value = clamp(number(params, 'ratio', 4), 1, 20);
    compressor.attack.value = number(params, 'attack', 10) / 1000; compressor.release.value = number(params, 'release', 120) / 1000;
    makeup.gain.value = 10 ** (number(params, 'makeup', 0) / 20); source.connect(compressor).connect(makeup).connect(output); return;
  }
  if (type === 'duck') {
    const gain = context.createGain(); source.connect(gain).connect(output);
    for (const event of duckAutomationEvents(params.pattern, number(params, 'depth', 10), number(params, 'release', 160), duration, tempo)) {
      gain.gain.setValueAtTime(event.floor, event.start); gain.gain.linearRampToValueAtTime(1, event.end);
    }
    return;
  }
  if (type === 'distortion' && workletReady(context)) {   // the same core as the live graph and the offline JS render
    const stage = new AudioWorkletNode(context, 'ample-distortion', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [2], channelCount: 2, channelCountMode: 'explicit', processorOptions: { params } });
    source.connect(stage).connect(output); return;
  }
  if (type === 'distortion') {
    const shaper = context.createWaveShaper(), tone = context.createBiquadFilter(), level = context.createGain();
    shaper.curve = distortionCurve(number(params, 'drive', 12), params.mode, 2049); shaper.oversample = '4x';
    tone.type = 'lowpass'; tone.frequency.value = number(params, 'tone', 8000); level.gain.value = 10 ** (number(params, 'level', 0) / 20);
    const effect = context.createGain(); source.connect(shaper).connect(tone).connect(level).connect(effect);
    wetDry(context, source, effect, output, number(params, 'mix', 100)); return;
  }
  if (type === 'delay') {
    // echo[n] = x[n-L] + feedback * echo[n-L] like the server: the first echo is one delay line, the repeats go round a second one. A delay inside a loop costs one render quantum (128 frames) extra, which the loop's delay takes off again.
    const delay = context.createDelay(2), loop = context.createDelay(2), feedback = context.createGain(), effect = context.createGain();
    const time = clamp(syncSamples(params, context.sampleRate, tempo) / context.sampleRate || number(params, 'time', 250) / 1000, .001, 2), quantum = 128 / context.sampleRate;
    delay.delayTime.value = time;
    loop.delayTime.value = Math.max(quantum, time - quantum);
    feedback.gain.value = clamp(number(params, 'feedback', 35) / 100, 0, .9);
    source.connect(delay).connect(effect); effect.connect(feedback).connect(loop).connect(effect);
    wetDry(context, source, effect, output, number(params, 'mix', 30), 'additive'); return;
  }
  if (type === 'chorus') {
    const delay = context.createDelay(.05), lfo = context.createOscillator(), lfoGain = context.createGain(), effect = context.createGain();
    lfo.type = 'triangle'; lfo.frequency.value = 1000 / clamp(number(params, 'period', 1500), 100, 10000);
    lfoGain.gain.value = clamp(number(params, 'depth', 5), 1, 15) / 1000;
    delay.delayTime.value = CHORUS_BASE_MS / 1000;
    lfo.connect(lfoGain).connect(delay.delayTime); lfo.start();
    source.connect(delay).connect(effect);
    wetDry(context, source, effect, output, number(params, 'mix', 50)); return;
  }
  const effect = context.createGain(), level = context.createGain(), update = connectReverb(context, source, effect, (value) => value);
  update(params); level.gain.value = clamp(number(params, 'mix', 25), 0, 100) / 100;
  source.connect(output); effect.connect(level).connect(output);   // as on the server: the dry sound stays, mix is the level of the room
}

export function encodeWav(buffer) {
  const channels = Math.min(2, buffer.numberOfChannels), sampleRate = buffer.sampleRate, frames = buffer.length;
  const bytes = new ArrayBuffer(44 + frames * channels * 2), view = new DataView(bytes);
  const write = (offset, text) => [...text].forEach((character, index) => view.setUint8(offset + index, character.charCodeAt(0)));
  write(0, 'RIFF'); view.setUint32(4, bytes.byteLength - 8, true); write(8, 'WAVE'); write(12, 'fmt '); view.setUint32(16, 16, true);
  view.setUint16(20, 1, true); view.setUint16(22, channels, true); view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * channels * 2, true); view.setUint16(32, channels * 2, true); view.setUint16(34, 16, true); write(36, 'data'); view.setUint32(40, frames * channels * 2, true);
  const data = Array.from({ length: channels }, (_, channel) => buffer.getChannelData(channel));
  let offset = 44;
  for (let frame = 0; frame < frames; frame++) for (let channel = 0; channel < channels; channel++) {
    const value = clamp(data[channel][frame], -1, 1); view.setInt16(offset, value < 0 ? value * 32768 : value * 32767, true); offset += 2;
  }
  return new Blob([bytes], { type: 'audio/wav' });
}

async function loadInput(takeId, source) {
  return takeCache.getDecoded(takeId, async () => {
    const blob = await source();
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio) throw new Error('Local audio preview is unavailable in this browser.');
    const context = new Audio();
    try { return await context.decodeAudioData(await blob.arrayBuffer()); } finally { await context.close(); }
  });
}

export const decodedInput = loadInput;

export async function decodePreviewBlob(blob) {
  if (!(blob instanceof Blob) || blob.size === 0) throw new Error('The renderer returned no preview audio.');
  const Audio = window.AudioContext || window.webkitAudioContext;
  if (!Audio) throw new Error('Audio analysis is unavailable in this browser.');
  const context = new Audio();
  try { return await context.decodeAudioData(await blob.arrayBuffer()); } finally { await context.close(); }
}

export async function renderApproximatePreview({ takeId, type, params, tempo, source }) {
  const input = await loadInput(takeId, source), Offline = window.OfflineAudioContext || window.webkitOfflineAudioContext;
  if (!Offline) throw new Error('Local audio preview is unavailable in this browser.');
  const duration = Math.min(30, input.duration), frames = Math.max(1, Math.floor(duration * input.sampleRate));
  const context = new Offline(input.numberOfChannels, frames, input.sampleRate), buffer = context.createBuffer(input.numberOfChannels, frames, input.sampleRate);
  for (let channel = 0; channel < input.numberOfChannels; channel++) buffer.copyToChannel(input.getChannelData(channel).subarray(0, frames), channel);
  const sourceNode = context.createBufferSource(), output = context.createGain(); sourceNode.buffer = buffer; output.connect(context.destination);
  effectGraph(context, sourceNode, output, type, params, Number(tempo) || 120, duration); sourceNode.start(0);
  return encodeWav(await context.startRendering());
}

export async function fetchTakeAudio(takeId, role = '') {
  if (!takeId) throw new Error('Build and select the upstream input before previewing this effect.');
  const response = await fetch(`/api/takes/${encodeURIComponent(takeId)}/download?format=wav${role ? `&role=${encodeURIComponent(role)}` : ''}`, { headers: { 'X-Ample-Token': getToken() } });
  if (!response.ok) throw new Error(`Could not read the selected input take (${response.status}).`);
  const blob = await response.blob();
  if (!(blob instanceof Blob) || blob.size === 0) throw new Error('The selected input take has no preview audio.');
  return blob;
}
