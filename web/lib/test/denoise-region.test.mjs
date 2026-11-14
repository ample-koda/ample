import test from 'node:test';
import assert from 'node:assert/strict';
import { fitRegion } from '../denoise-region.mjs';

test('a region inside the input is only rounded to tenths of a second', () => {
  assert.deepEqual(fitRegion(1.04, 2.26, 10), { from: 1, length: 2.3, moved: false });
  assert.deepEqual(fitRegion(1, 2, 10), { from: 1, length: 2, moved: false });
});

test('a short selection near the end is moved earlier and keeps its length (issue 184)', () => {
  assert.deepEqual(fitRegion(4.98, 0.1, 5), { from: 4.9, length: 0.1, moved: true });
  const fit = fitRegion(9.97, 0.5, 10);
  assert.ok(fit.from + fit.length <= 10, 'the end is inside the input');
  assert.equal(fit.length, 0.5);
  assert.equal(fit.from, 9.5);
});

test('a selection longer than the input is the whole input', () => {
  assert.deepEqual(fitRegion(0, 50, 5.04), { from: 0, length: 5, moved: true });
});

test('a selection that starts before the input starts at its beginning', () => {
  assert.deepEqual(fitRegion(-1, 1, 10), { from: 0, length: 1, moved: false });
});

test('the shortest selection is a tenth of a second', () => {
  assert.deepEqual(fitRegion(2, 0.01, 10), { from: 2, length: 0.1, moved: false });
});

test('with no known length of the input nothing is moved, and an input shorter than the minimum is taken whole', () => {
  assert.deepEqual(fitRegion(2, 0.2, 0), { from: 2, length: 0.2, moved: false });
  assert.deepEqual(fitRegion(0, 0.1, 0.05), { from: 0, length: 0.05, moved: true });
});
