import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

// Renders a stereo test signal through the persistent live graph (liveEffectGraph) in an OfflineAudioContext and reports the level per channel and per 0.25 s window.
async function render(page, type: string, params: Record<string, string>, tone = [440, 660], seconds = 1) {
  return page.evaluate(async ({ type, params, tone, seconds }) => {
    const { liveEffectGraph } = await import('/app/studio/preview/audio.js');
    const rate = 44100, context = new OfflineAudioContext(2, rate * seconds, rate), buffer = context.createBuffer(2, rate * seconds, rate);
    for (let c = 0; c < 2; c++) { const data = buffer.getChannelData(c); for (let i = 0; i < data.length; i++) data[i] = Math.sin(2 * Math.PI * tone[c] * i / rate) * .5; }
    const source = context.createBufferSource(); source.buffer = buffer; source.loop = false;
    const out = context.createGain(); out.connect(context.destination);
    liveEffectGraph(context, source, out, type, params, 120); source.start();
    const result = await context.startRendering();
    const rms = (data: Float32Array, a = 0, b = data.length) => { let s = 0; for (let i = a; i < b; i++) s += data[i] * data[i]; return Math.sqrt(s / (b - a)); };
    const left = result.getChannelData(0), right = result.getChannelData(1), windows: number[] = [], quarter = rate / 4;
    for (let w = 0; w + quarter <= left.length; w += quarter) windows.push(rms(left, w, w + quarter));
    let diff = 0; for (let i = 0; i < left.length; i++) diff += (left[i] - right[i]) ** 2;
    const half = Math.floor(left.length / 2);   // the second half: the knob smoothing (10 ms) has settled
    return { left: rms(left, half, left.length), right: rms(right, half, left.length), windows, sideDiff: Math.sqrt(diff / left.length), first: Array.from(left.slice(0, 2000)) };
  }, { type, params, tone, seconds });
}

test('@audio pan, width, phaser, flanger and the filter LFO have live graphs that change the sound', async ({ page }) => {
  await openApp(page);
  const flat = await render(page, 'pan', { pan: '0' });
  const hardLeft = await render(page, 'pan', { pan: '-32' }), hardRight = await render(page, 'pan', { pan: '32' });
  expect(flat.left).toBeGreaterThan(.3); expect(hardLeft.right).toBeLessThan(.01); expect(hardLeft.left).toBeGreaterThan(.3);
  expect(hardRight.left).toBeLessThan(.01); expect(hardRight.right).toBeGreaterThan(.3);

  const same = await render(page, 'width', { width: '100' }), mono = await render(page, 'width', { width: '0' });
  expect(same.sideDiff).toBeGreaterThan(.3); expect(mono.sideDiff).toBeLessThan(.08);

  const dry = await render(page, 'phaser', { stages: '4', period: '500 ms', mix: '0' }), wet = await render(page, 'phaser', { stages: '4', period: '500 ms', mix: '100' });
  const phaserDiff = wet.first.reduce((sum, value, index) => sum + Math.abs(value - dry.first[index]), 0);
  expect(phaserDiff).toBeGreaterThan(5);
  const flanger = await render(page, 'flanger', { depth: '2 ms', period: '500 ms', feedback: '50', mix: '100' });
  expect(flanger.left).toBeGreaterThan(.05); expect(Math.abs(flanger.first.reduce((s, v, i) => s + Math.abs(v - dry.first[i]), 0))).toBeGreaterThan(5);

  const wobble = await render(page, 'filter', { type: 'lowpass', cutoff: '500 Hz', slope: '12', lfo_rate: '1/4', lfo_depth: '4 oct' }, [2000, 2000], 2);
  const still = await render(page, 'filter', { type: 'lowpass', cutoff: '500 Hz', slope: '12', lfo_rate: 'off', lfo_depth: '4 oct' }, [2000, 2000], 2);
  // the cutoff swings up to four octaves above 500 Hz, so a 2 kHz tone is far louder than with the cutoff held still (windows after the start-up transient)
  expect(Math.max(...wobble.windows.slice(1))).toBeGreaterThan(Math.max(...still.windows.slice(1)) * 5);
});

test('@audio duck and tremolo build on the audio clock without a timer', async ({ page }) => {
  await openApp(page);
  const result = await page.evaluate(async () => {
    const { liveEffectGraph } = await import('/app/studio/preview/audio.js');
    const context = new OfflineAudioContext(2, 44100, 44100), source = context.createGain(), out = context.createGain();
    const made = [liveEffectGraph(context, source, out, 'duck', { pattern: 'x...', depth: '12', release: '160' }, 120), liveEffectGraph(context, source, out, 'tremolo', { shape: 'gate', pattern: 'x.x.', depth: '50', sync: '1/8' }, 120)];
    made.forEach((graph) => { graph.update({ pattern: 'x.x.', depth: '12', release: '100', shape: 'gate', sync: '1/8' }); graph.dispose(); });
    return made.length;
  });
  expect(result).toBe(2);
});

test('@audio the live reverb is the offline reverb: the worklet gives the same samples as the render (same core)', async ({ page }) => {
  await openApp(page);
  const result = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const { processReverbBuffer } = await import('/lib/clientside-reverb.mjs');
    const rate = 44100, frames = rate, context = new OfflineAudioContext(2, frames, rate), buffer = context.createBuffer(2, frames, rate);
    const input = [new Float32Array(frames), new Float32Array(frames)];
    let x = 7; for (let c = 0; c < 2; c++) for (let i = 0; i < frames; i++) { x = (x * 1664525 + 1013904223) >>> 0; input[c][i] = ((x / 2 ** 32) * 2 - 1) * (i < 6000 ? .6 : 0); buffer.getChannelData(c)[i] = input[c][i]; }
    const ready = await prepareWorklets(context);
    const params = { size: '63', decay: '80', mix: '40' };
    const source = context.createBufferSource(); source.buffer = buffer; const out = context.createGain(); out.connect(context.destination);
    liveEffectGraph(context, source, out, 'reverb', params, 120); source.start();
    const live = await context.startRendering();
    const offline = processReverbBuffer(input, params, rate);
    let worst = 0;
    for (let c = 0; c < 2; c++) { const data = live.getChannelData(c); for (let i = 0; i < frames; i++) worst = Math.max(worst, Math.abs(data[i] - offline[c][i])); }
    return { ready, worst };
  });
  expect(result.ready).toBe(true);
  expect(result.worst).toBeLessThan(1e-6);
});

test('@audio the live gate, limiter and de-esser are the offline ones: the worklets give the same samples as the render cores', async ({ page }) => {
  await openApp(page);
  const result = await page.evaluate(async () => {
    const { liveEffectGraph, prepareWorklets } = await import('/app/studio/preview/audio.js');
    const { gateAudio } = await import('/lib/clientside-gate.mjs');
    const { limiterAudio } = await import('/lib/clientside-limiter.mjs');
    const { deesserAudio } = await import('/lib/clientside-deesser.mjs');
    const offline = { gate: gateAudio, limiter: limiterAudio, deesser: deesserAudio };
    const cases = { gate: { threshold: '-30 dB', attack: '5 ms', hold: '40 ms', release: '100 ms' }, limiter: { ceiling: '-6 dB', lookahead: '5 ms', release: '100 ms' }, deesser: { frequency: '6000 Hz', threshold: '-40 dB', ratio: '6' } };
    const rate = 44100, frames = rate, report: Record<string, { worst: number; latency: number }> = {};
    for (const [type, params] of Object.entries(cases)) {
      const context = new OfflineAudioContext(2, frames, rate), buffer = context.createBuffer(2, frames, rate), input = [new Float32Array(frames), new Float32Array(frames)];
      let x = 11; for (let c = 0; c < 2; c++) for (let i = 0; i < frames; i++) { x = (x * 1664525 + 1013904223) >>> 0; input[c][i] = ((x / 2 ** 32) * 2 - 1) * (Math.floor(i / 5000) % 2 ? .9 : .03); buffer.getChannelData(c).set([input[c][i]], i); }
      await prepareWorklets(context);
      const source = context.createBufferSource(); source.buffer = buffer; const out = context.createGain(); out.connect(context.destination);
      const graph = liveEffectGraph(context, source, out, type, params, 120); source.start();
      const live = await context.startRendering();
      const reference = await offline[type]({ sampleRate: rate, channels: input }, params);
      const delay = Math.round(graph.latency * rate);   // the limiter's look-ahead is a delay of the live sound; the render compensates it
      let worst = 0;
      for (let c = 0; c < 2; c++) { const data = live.getChannelData(c); for (let i = 0; i < frames - delay; i++) worst = Math.max(worst, Math.abs(data[i + delay] - reference.channels[c][i])); }
      report[type] = { worst, latency: delay };
    }
    return report;
  });
  for (const type of ['gate', 'limiter', 'deesser']) expect(result[type].worst, type).toBeLessThan(1e-6);
  expect(result.limiter.latency).toBeGreaterThan(0);
});
