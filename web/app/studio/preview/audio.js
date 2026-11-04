import { getToken } from '../../api.js';
import { filterPasses } from '../../../lib/studio-math.js';
import { gateLevel } from '../../../lib/trance-gate.mjs';
import { barBeats } from '../../../lib/meter.mjs';

const sourceCache = new Map();
const effectTypes = new Set(['eq', 'peq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb', 'volume', 'chorus', 'tremolo']);
const CHORUS_BASE_MS = 20;   // same fixed base delay as the server's ModDelay(x, rate, 20, depth, period, 0, mix)
const number = (params, name, fallback) => {
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

// Persistent nodes: changing a knob never replaces or restarts the playing sample.
export function liveEffectGraph(context, source, output, type, initial, tempo, impulse) {
  const nodes = [], node = (value) => { nodes.push(value); return value; };
  const dry = node(context.createGain()), wet = node(context.createGain()), input = node(context.createGain());
  source.connect(dry).connect(output); source.connect(input); wet.connect(output);
  const set = (param, value) => { param.cancelScheduledValues(context.currentTime); param.setTargetAtTime(value, context.currentTime, .01); };
  let updateEffect, updateMix = () => {}, replaceImpulse = null, timer = null, oscillators = [];
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
    updateEffect = (params) => {
      const kind = ['highpass', 'bandpass', 'notch'].includes(params.type) ? params.type : 'lowpass', q = 1 + number(params, 'resonance', 0) * 0.17, passes = filterPasses(params.slope);
      stages.forEach((stage, index) => { stage.type = index < passes ? kind : 'allpass'; set(stage.frequency, number(params, 'cutoff', 1000)); set(stage.Q, q); });
    };
  } else if (type === 'compressor') {
    const compressor = node(context.createDynamicsCompressor()), makeup = node(context.createGain()); input.connect(compressor).connect(makeup).connect(wet);
    updateEffect = (params) => {
      set(compressor.threshold, number(params, 'threshold', -18)); set(compressor.ratio, clamp(number(params, 'ratio', 4), 1, 20));
      set(compressor.attack, number(params, 'attack', 10) / 1000); set(compressor.release, number(params, 'release', 120) / 1000); set(makeup.gain, 10 ** (number(params, 'makeup', 0) / 20));
    };
  } else if (type === 'duck') {
    const gain = node(context.createGain()); input.connect(gain).connect(wet);
    let params = initial;
    const tick = () => {
      const stepDuration = 60 / Math.max(1, Number(tempo) || 120) / 4;
      const position = window.AMPLE_PLAYER.state().time, phase = position % stepDuration;
      const pattern = String(params.pattern || 'x...x...x...x...').padEnd(16, '.').slice(0, 16);
      const floor = 10 ** (-clamp(number(params, 'depth', 10), 0, 40) / 20), release = Math.max(.001, number(params, 'release', 160) / 1000);
      set(gain.gain, pattern[Math.floor(position / stepDuration) % 16] === 'x' ? floor + (1 - floor) * Math.min(1, phase / release) : 1);
    };
    updateEffect = (values) => { params = values; tick(); }; timer = setInterval(tick, 15);
  } else if (type === 'tremolo') {
    // shape gate: the gain follows the step pattern from the playhead (like duck); the other shapes are an LFO on the gain
    const gain = node(context.createGain()), lfo = context.createOscillator(), lfoGain = node(context.createGain());
    lfo.connect(lfoGain).connect(gain.gain); lfo.start(); oscillators.push(lfo); nodes.push(lfo);
    input.connect(gain).connect(wet);
    let params = initial;
    const gateTick = () => {
      if (params.shape !== 'gate') return;
      const stepSeconds = 60 / Math.max(1, Number(tempo) || 120) * 4 / liveStepDivision(params), position = window.AMPLE_PLAYER.state().time;
      const lfoValue = gateLevel(String(params.pattern ?? 'x.x.x.x.x.x.x.x.').replace(/^"|"$/g, ''), position / stepSeconds * 1000, 1000, Math.min(500, .002 / stepSeconds * 1000));
      set(gain.gain, 1 - clamp(number(params, 'depth', 50), 0, 100) / 100 * (1 - lfoValue) / 2);
    };
    updateEffect = (values) => {
      params = values;
      const gate = values.shape === 'gate', depth = clamp(number(values, 'depth', 50), 0, 100) / 100, division = liveStepDivision(values);
      const hz = values.sync && values.sync !== 'off' ? division * Math.max(1, Number(tempo) || 120) / (barBeats() * 60) : clamp(number(values, 'rate', 4), .05, 40);
      lfo.type = values.shape === 'square' ? 'square' : values.shape === 'triangle' ? 'triangle' : 'sine';
      set(lfo.frequency, hz); set(lfoGain.gain, gate ? 0 : depth / 2);
      if (gate) gateTick(); else set(gain.gain, 1 - depth / 2);
    };
    timer = setInterval(gateTick, 15);
  } else if (type === 'distortion') {
    const shaper = node(context.createWaveShaper()), tone = node(context.createBiquadFilter()), level = node(context.createGain());
    shaper.oversample = '4x'; tone.type = 'lowpass'; input.connect(shaper).connect(tone).connect(level).connect(wet);
    let shapeKey = '';
    updateEffect = (params) => {
      const key = `${params.drive}:${params.mode}`;
      if (key !== shapeKey) { shaper.curve = distortionCurve(number(params, 'drive', 12), params.mode, 2049); shapeKey = key; }
      set(tone.frequency, number(params, 'tone', 8000)); set(level.gain, 10 ** (number(params, 'level', 0) / 20));
    };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 100), 0, 100) / 100; set(dry.gain, Math.cos(amount * Math.PI / 2)); set(wet.gain, Math.sin(amount * Math.PI / 2)); };
  } else if (type === 'delay') {
    const delay = node(context.createDelay(2)), feedback = node(context.createGain()); input.connect(delay).connect(wet); delay.connect(feedback).connect(delay);
    updateEffect = (params) => { set(delay.delayTime, clamp(number(params, 'time', 250) / 1000, .001, 2)); set(feedback.gain, clamp(number(params, 'feedback', 35) / 100, 0, .9)); };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 30), 0, 100) / 100; set(dry.gain, Math.cos(amount * Math.PI / 2)); set(wet.gain, Math.sin(amount * Math.PI / 2)); };
  } else if (type === 'reverb') {
    updateEffect = connectReverb(context, input, wet, node);
    updateMix = (params) => { set(dry.gain, 1); set(wet.gain, clamp(number(params, 'mix', 25), 0, 100) / 100); };   // as on the server: the dry sound stays, mix is the level of the room
  } else if (type === 'chorus') {
    // a delay line modulated by a triangle LFO, matching the server's fixed 20 ms base delay with depth/period swept on top (no feedback, unlike flanger)
    const delay = node(context.createDelay(.05)), lfo = context.createOscillator(), lfoGain = node(context.createGain());
    lfo.type = 'triangle'; lfo.connect(lfoGain).connect(delay.delayTime); lfo.start(); oscillators.push(lfo); nodes.push(lfo);
    input.connect(delay).connect(wet);
    updateEffect = (params) => {
      set(delay.delayTime, CHORUS_BASE_MS / 1000);
      set(lfoGain.gain, clamp(number(params, 'depth', 5), 1, 15) / 1000);
      set(lfo.frequency, 1000 / clamp(number(params, 'period', 1500), 100, 10000));
    };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 50), 0, 100) / 100; set(dry.gain, Math.cos(amount * Math.PI / 2)); set(wet.gain, Math.sin(amount * Math.PI / 2)); };
  } else if (type === 'convolution') {
    if (!impulse) throw new Error('Choose an impulse response before previewing convolution.');
    const predelay = node(context.createDelay(.5)), convolver = node(context.createConvolver()), level = node(context.createGain());
    convolver.normalize = false; input.connect(predelay).connect(convolver).connect(level).connect(wet);
    let impulseKey = '', currentImpulse = impulse;
    updateEffect = (params) => {
      const key = `${params.length}:${params.normalize}`;
      if (key !== impulseKey) { convolver.buffer = convolutionImpulse(context, currentImpulse, params); impulseKey = key; }
      set(predelay.delayTime, clamp(number(params, 'predelay', 0) / 1000, 0, .5));
      set(level.gain, 10 ** (number(params, 'level', 0) / 20));
    };
    replaceImpulse = (next, params) => { currentImpulse = next; impulseKey = ''; updateEffect(params); };
    updateMix = (params) => { const amount = clamp(number(params, 'mix', 30), 0, 100) / 100; set(dry.gain, 1 - amount); set(wet.gain, amount); };
  } else { input.connect(wet); updateEffect = () => {}; }
  const update = (params) => {
    updateEffect(params); set(dry.gain, 0); set(wet.gain, 1); updateMix(params);
    if (params.enabled === 'off') { set(dry.gain, 1); set(wet.gain, 0); }
  };
  update(initial);
  // oscillators must be stopped, not just disconnected, or they keep running silently on the shared meter context across every later preview
  return { update, replaceImpulse, dispose: () => { if (timer !== null) clearInterval(timer); oscillators.forEach((value) => value.stop()); nodes.forEach((value) => value.disconnect()); } };
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
    curve[index] = mode === 'hard' ? clamp(driven, -1, 1) : Math.tanh(driven);
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

function wetDry(context, input, effect, output, mix) {
  const dry = context.createGain(), wet = context.createGain(), amount = clamp(mix, 0, 100) / 100;
  dry.gain.value = Math.cos(amount * Math.PI / 2);
  wet.gain.value = Math.sin(amount * Math.PI / 2);
  input.connect(dry).connect(output);
  input.connect(effect).connect(wet).connect(output);
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
  if (type === 'distortion') {
    const shaper = context.createWaveShaper(), tone = context.createBiquadFilter(), level = context.createGain();
    shaper.curve = distortionCurve(number(params, 'drive', 12), params.mode, 2049); shaper.oversample = '4x';
    tone.type = 'lowpass'; tone.frequency.value = number(params, 'tone', 8000); level.gain.value = 10 ** (number(params, 'level', 0) / 20);
    const effect = context.createGain(); source.connect(shaper).connect(tone).connect(level).connect(effect);
    wetDry(context, source, effect, output, number(params, 'mix', 100)); return;
  }
  if (type === 'delay') {
    const delay = context.createDelay(2), feedback = context.createGain(), effect = context.createGain();
    delay.delayTime.value = clamp(number(params, 'time', 250) / 1000, .001, 2);
    feedback.gain.value = clamp(number(params, 'feedback', 35) / 100, 0, .9);
    source.connect(delay); delay.connect(feedback).connect(delay); delay.connect(effect);
    wetDry(context, source, effect, output, number(params, 'mix', 30)); return;
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
  if (!sourceCache.has(takeId)) sourceCache.set(takeId, source().then(async (blob) => {
    const Audio = window.AudioContext || window.webkitAudioContext;
    if (!Audio) throw new Error('Local audio preview is unavailable in this browser.');
    const context = new Audio();
    try { return await context.decodeAudioData(await blob.arrayBuffer()); } finally { await context.close(); }
  }).catch((error) => { sourceCache.delete(takeId); throw error; }));
  return sourceCache.get(takeId);
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

export async function fetchTakeAudio(takeId) {
  if (!takeId) throw new Error('Build and select the upstream input before previewing this effect.');
  const response = await fetch(`/api/takes/${encodeURIComponent(takeId)}/download?format=wav`, { headers: { 'X-Ample-Token': getToken() } });
  if (!response.ok) throw new Error(`Could not read the selected input take (${response.status}).`);
  const blob = await response.blob();
  if (!(blob instanceof Blob) || blob.size === 0) throw new Error('The selected input take has no preview audio.');
  return blob;
}
