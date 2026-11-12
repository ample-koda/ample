import test from 'node:test';
import assert from 'node:assert/strict';
import { triangleWave, phaserWave, filterWave, tremoloWave, fillWave } from '../live-lfo.mjs';
import { phaserAudio } from '../clientside-phaser.mjs';

test('the modulation triangle starts at -1, peaks at half a period and repeats', () => {
  const w = triangleWave(1000);
  assert.equal(w(0), -1);
  assert.equal(w(500), 1);
  assert.equal(w(1000), -1);
  assert.ok(Math.abs(w(250)) < 1e-9);
});

test('the phaser triangle is the integer one of the render (32767 steps)', () => {
  const w = phaserWave(2000);
  assert.equal(w(0), -1);
  assert.equal(w(1000), 1);
});

test('the filter LFO starts low for every shape except square, which starts low too (0)', () => {
  for (const shape of ['sine', 'triangle', 'saw', 'square']) assert.equal(filterWave(shape, 4, 96000)(0), -1, shape);
  assert.ok(Math.abs(filterWave('sine', 4, 96000)(12000) - 1) < 1e-9);   // half a cycle
});

test('the tremolo triangle starts high and the sine starts at zero, with phase', () => {
  assert.equal(tremoloWave('triangle', 4, 48000)(0), 1);
  assert.ok(Math.abs(tremoloWave('sine', 4, 48000)(0)) < 1e-12);
  assert.ok(Math.abs(tremoloWave('sine', 4, 48000, 90)(0) - 1) < 1e-9);
  assert.equal(tremoloWave('square', 4, 48000)(0), 1);
});

test('fillWave writes the wave', () => {
  const t = fillWave(new Float32Array(4), (i) => i * 2);
  assert.deepEqual([...t], [0, 2, 4, 6]);
});
