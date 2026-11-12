import test from 'node:test';
import assert from 'node:assert/strict';
import { bandCentres, logBands, peakHold, scopeStart } from '../spectrum-math.mjs';

test('the centres are log spaced from 20 Hz to 20 kHz', () => {
  const c = bandCentres(4);
  assert.ok(Math.abs(c[0] - 20) < 1e-9 && Math.abs(c[3] - 20000) < 1e-6);
  assert.ok(Math.abs(c[1] / c[0] - c[2] / c[1]) < 1e-9);
});

test('a single loud bin shows up in the band that contains it and not far away', () => {
  const bins = new Float32Array(1025).fill(-120), rate = 48000, hz = rate / 2 / 1024;
  bins[Math.round(1000 / hz)] = -10;
  const centres = [250, 1000, 4000], out = logBands(bins, rate, centres, 1 / 3);
  assert.ok(out[1] > -25);
  assert.ok(out[0] < -100 && out[2] < -100);
});

test('peak hold falls slowly and follows a rise at once', () => {
  assert.deepEqual(peakHold([-10, -50], [-30, -20], 1), [-11, -20]);
});

test('a scope starts on a rising zero crossing', () => {
  const s = new Float32Array(64);
  for (let i = 0; i < 64; i++) s[i] = Math.sin(2 * Math.PI * (i + 3.5) / 16);
  const start = scopeStart(s);
  assert.ok(s[start - 1] < 0 && s[start] >= 0);
});
