import test from 'node:test';
import assert from 'node:assert/strict';
import { panGainsQ15, panPoints, panCurve } from '../live-pan-auto.mjs';
import { panAudio } from '../clientside-pan.mjs';
import { loadVectors, serverChannels, quantizedInput, deviationDb } from '../dsp-conformance.mjs';

const vectors = loadVectors(), bar = 88200;   // a bar at 120 bpm, 44.1 kHz

test('the pan table gains are the integer ones of the server (PanTable): a stereo sound keeps the near side, centre is 1', () => {
  assert.deepEqual(panGainsQ15(0), { left: 32768, right: 32768 });
  assert.equal(panGainsQ15(-32).right, 0);
  assert.equal(panGainsQ15(32).left, 0);
  assert.equal(panGainsQ15(16).left, 23170);   // floor(sqrt(16 / 64) * 32768) over the centre gain
  assert.equal(panGainsQ15(0, false).left, 23170);   // mono: the power split itself
});

test('lfo, auto and env are read like Automation.cs (bars, decimal bars, seconds, whole numbers only)', () => {
  const lfo = panPoints('lfo(32, -4, 4)', bar, 44100);
  assert.deepEqual(lfo.slice(0, 3), [{ sample: 0, value: -4 }, { sample: 16 * bar, value: 4 }, { sample: 32 * bar, value: -4 }]);
  assert.equal(lfo.length, 17);   // a point every 16 bars up to bar 257
  assert.deepEqual(panPoints('auto(31.5s:-6, 5.5:0)', bar, 44100), [{ sample: Math.round(4.5 * bar), value: 0 }, { sample: Math.round(31.5 * 44100), value: -6 }]);
  assert.deepEqual(panPoints('env(1, 2, 4, 2, -8, 8)', bar, 44100).map((p) => [p.sample / bar, p.value]), [[0, -8], [2, 8], [6, 8], [8, -8]]);
  for (const bad of ['lfo(3, 1, 2)', 'lfo(32, 1.5, 2)', 'auto(0:1)', 'auto(1:x)', 'env(1,2,3)', '16', 'fadein(1, 2)', '']) assert.equal(panPoints(bad, bar, 44100), null, bad);
});

test('the curve joins the gains of the points by straight lines, not the pan positions, and counts from the first bar of the take', () => {
  const points = panPoints('auto(1:-32, 3:32)', bar, 44100), left = panCurve(points, 1, bar, 'left'), right = panCurve(points, 1, bar, 'right');
  assert.equal(left(0), 1); assert.equal(right(0), 0);
  assert.ok(Math.abs(left(bar) - 0.5) < 1e-4 && Math.abs(right(bar) - 0.5) < 1e-4, 'halfway the gains are 0.5 and 0.5 (the positions would give 0.707)');
  assert.equal(left(2 * bar), 0); assert.equal(right(5 * bar), 1);   // after the last point it stays
  const later = panCurve(points, 2, bar, 'left');   // a take that starts at bar 2: sample 0 is the song position of bar 2
  assert.ok(Math.abs(later(0) - 0.5) < 1e-4);
  assert.ok(Math.abs(later(bar * 3) - 0) < 1e-9 && later(100) > later(1000) - 1);   // jumping back is allowed
});

test('the live curve and the offline render are the server render of an automated pan (vectors pan~lfo, pan~auto)', async () => {
  for (const key of ['pan~lfo', 'pan~auto']) {
    const input = quantizedInput(), entry = vectors.cases[key];
    const offline = await panAudio({ sampleRate: input.sampleRate, channels: input.channels.map((c) => c.slice()) }, entry.params, undefined, entry.tempo, 1);
    const left = panCurve(panPoints(entry.params.pan, bar, 44100), 1, bar, 'left'), right = panCurve(panPoints(entry.params.pan, bar, 44100), 1, bar, 'right');
    const live = [input.channels[0].map((v, i) => v * left(i)), input.channels[1].map((v, i) => v * right(i))];
    assert.ok(deviationDb(serverChannels(entry), offline.channels, input.sampleRate) <= -75, key + ' offline');
    assert.ok(deviationDb(serverChannels(entry), live, input.sampleRate) <= -75, key + ' live');
  }
});
