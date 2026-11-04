import test from 'node:test';
import assert from 'node:assert/strict';
import { biquadMagnitude, clamp, compressorOutput, delayTapGain, denormalize, distortionSample, duckGain, equalizerResponse, estimatedRt60, formatValue, interpolateCurve, knobDragValue, normalize, snap, spectrumBins } from '../studio-math.js';

test('linear and logarithmic scales round trip', () => {
  for (const scale of ['linear', 'log']) {
    const min = scale === 'log' ? 20 : -12; const max = scale === 'log' ? 20000 : 12;
    for (const value of [min, min + (max - min) * .25, min + (max - min) * .75, max]) assert.ok(Math.abs(denormalize(normalize(value, min, max, scale), min, max, scale) - value) < 1e-8);
  }
});

test('studio math clamps and snaps relative to the range start', () => {
  assert.equal(clamp(12, 0, 10), 10);
  assert.equal(snap(7, 2, 1, 10), 7);
  assert.equal(snap(-2, 2, 1, 10), 1);
});

test('knob pointer travel is linear in value space for every scale', () => {
  assert.equal(knobDragValue(1000, 100, 20, 20020), 11000);
  assert.equal(knobDragValue(1000, 100, 20, 20020, true), 2000);
  assert.equal(knobDragValue(19000, 200, 20, 20020), 20020);
  assert.equal(knobDragValue(100, -200, 20, 20020), 20);
});

test('studio values format units and interpolate curves', () => {
  assert.equal(formatValue(1.5, 2, 'kHz'), '1.5 kHz');
  assert.equal(interpolateCurve([{ x: 0, y: 0 }, { x: 10, y: 20 }], 5), 10);
  assert.ok(Math.abs(compressorOutput(-6, -18, 4) - -13.16) < 0.01);
});

test('EQ and filter response curves are flat at unity and roll off on the intended side', () => {
  assert.ok(Math.abs(equalizerResponse(1000, { low: 0, mid: 0, high: 0 })) < 1e-8);
  assert.ok(biquadMagnitude('lowpass', 100, 1000) > biquadMagnitude('lowpass', 10000, 1000));
  assert.ok(biquadMagnitude('highpass', 100, 1000) < biquadMagnitude('highpass', 10000, 1000));
  assert.ok(Math.abs(equalizerResponse(100, { low: 6, mid: 0, high: 0, low_hz: 250, high_hz: 3000 }) - 6) < 1.2);
});

test('spectrum estimate locates a generated tone and stays within the display range', () => {
  const samples = Float32Array.from({ length: 2048 }, (_, index) => Math.sin(2 * Math.PI * 1000 * index / 48000));
  const bins = spectrumBins(samples, 48000, 96), loudest = bins.reduce((best, bin) => bin.db > best.db ? bin : best);
  assert.ok(loudest.frequency > 800 && loudest.frequency < 1250);
  assert.ok(bins.every((bin) => bin.db >= -72 && bin.db <= 6));
  assert.deepEqual(spectrumBins([], 48000, 96), []);
});

test('duck envelope reaches its depth at each hit and releases to unity', () => {
  assert.ok(Math.abs(duckGain(0, 'x...............', 12, 4) - 10 ** (-12 / 20)) < 1e-8);
  assert.ok(Math.abs(duckGain(4, 'x...............', 12, 4) - 1) < 1e-8);
  assert.ok(Math.abs(duckGain(4, 'x...x...........', 6, 2) - 10 ** (-6 / 20)) < 1e-8);
});

test('compressor transfer curve preserves levels below threshold and applies the declared ratio', () => {
  assert.equal(compressorOutput(-24, -18, 4), -24);
  assert.ok(Math.abs(compressorOutput(-14, -18, 4) - -16.81) < 0.02);
  const integerFixtureInputDb = 20 * Math.log10(16000 / 32768);
  assert.ok(Math.abs(compressorOutput(integerFixtureInputDb, -18, 4) - -13.3) < 0.1);
});

test('distortion, delay and reverb display plots stay bounded and decay', () => {
  assert.ok(Math.abs(distortionSample(0.5, 0, 'soft')) < 0.5);
  assert.equal(distortionSample(2, 12, 'hard'), 1);
  assert.ok(delayTapGain(50, 3) < delayTapGain(50, 1));
  assert.ok(estimatedRt60(80, 80) > estimatedRt60(30, 30));
});

test('the equalizer response reads parameter texts with units (the line was flat for "-12 dB")', async () => {
  const { equalizerResponse } = await import('../studio-math.js');
  const params = { low: '-12 dB', mid: '10 dB', high: '0 dB', low_hz: '1034 Hz', high_hz: '4760 Hz' };
  assert.ok(equalizerResponse(30, params) < -10);
  assert.ok(Math.abs(equalizerResponse(1000, params)) > 1);
  assert.equal(Math.round(equalizerResponse(1000, { low: 0, mid: 0, high: 0 })), 0);
});

test('the parametric EQ response is flat at 0 dB and a peak band lifts its own frequency', async () => {
  const { peqResponse, bandOf } = await import('../studio-math.js');
  for (const hz of [50, 400, 3000, 12000]) assert.ok(Math.abs(peqResponse(hz, {})) < 0.01);
  const boosted = { band3_gain: '9 dB', band3_hz: '800 Hz', band3_q: '2' };
  assert.ok(peqResponse(800, boosted) > 8.5 && peqResponse(800, boosted) < 9.5);
  assert.ok(peqResponse(50, boosted) < 1);
  assert.deepEqual(bandOf({ band1_type: 'notch', band1_hz: '100 Hz' }, 1), { type: 'notch', hz: 100, gain: 0, q: 1, enabled: true });
});
