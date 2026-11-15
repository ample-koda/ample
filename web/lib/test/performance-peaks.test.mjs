import test from 'node:test';
import assert from 'node:assert/strict';
import { loopFraction, peaksOf, scalePeaks } from '../performance-peaks.mjs';

test('the peaks are the loudest value of every column, over all channels', () => {
  const left = Float32Array.from([0, 0.5, 0, 0, -0.25, 0, 0, 0]);
  const right = Float32Array.from([0, 0, 0.75, 0, 0, 0, -0.9, 0]);
  const peaks = peaksOf([left, right], 4);
  assert.deepEqual([...peaks].map((p) => Math.round(p * 100) / 100), [0.5, 0.75, 0.25, 0.9]);
});

test('a sound shorter than the columns, an empty sound and a long one all give the columns asked for', () => {
  assert.equal(peaksOf([Float32Array.from([1, 0.5])], 5).length, 5);
  assert.deepEqual([...peaksOf([], 3)], [0, 0, 0]);
  const long = new Float32Array(2_000_000); long[1_500_000] = 0.8;
  const peaks = peaksOf([long], 100);
  assert.equal(peaks.length, 100);
  assert.ok(peaks[75] > 0 || peaks.some((p) => p > 0), 'the spike is seen somewhere');
});

test('scaling makes the loudest column one and leaves silence alone', () => {
  assert.deepEqual([...scalePeaks(Float32Array.from([0.1, 0.4, 0.2]))].map((p) => Math.round(p * 100) / 100), [0.25, 1, 0.5]);
  assert.deepEqual([...scalePeaks(Float32Array.from([0, 0]))], [0, 0]);
});

test('the loop fraction wraps and a clip that has not started is at the start', () => {
  assert.equal(loopFraction(0, 4), 0);
  assert.equal(loopFraction(-1, 4), 0);
  assert.equal(loopFraction(1, 4), 0.25);
  assert.equal(loopFraction(9, 4), 0.25);
  assert.equal(loopFraction(3, 0), 0);
});
