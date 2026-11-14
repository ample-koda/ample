import test from 'node:test';
import assert from 'node:assert/strict';
import { drawnRange, isDrag } from '../playlist-draw.mjs';

test('a drag to the right draws from the first bar to the bar under the pointer (issue 107)', () => {
  assert.deepEqual(drawnRange(6, 8), { start: 6, length: 3 });
});

test('a drag within one bar draws a one-bar clip', () => {
  assert.deepEqual(drawnRange(6, 6), { start: 6, length: 1 });
});

test('a drag to the left draws from the bar under the pointer to the first bar', () => {
  assert.deepEqual(drawnRange(8, 6), { start: 6, length: 3 });
});

test('a clip never starts before bar 1 and a bad bar counts as 1', () => {
  assert.deepEqual(drawnRange(3, -2), { start: 1, length: 3 });
  assert.deepEqual(drawnRange(NaN, 2), { start: 1, length: 2 });
});

test('only a real movement is a drag, so a click draws nothing', () => {
  assert.equal(isDrag(100, 102), false);
  assert.equal(isDrag(100, 104), true);
  assert.equal(isDrag(100, 90), true);
});
