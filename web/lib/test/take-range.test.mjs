import test from 'node:test';
import assert from 'node:assert/strict';
import { editRange, parseBar } from '../take-range.mjs';

const cur = { from: 2, to: 4 };
test('parseBar accepts whole numbers and rejects the rest', () => {
  assert.equal(parseBar('3'), 3); assert.equal(parseBar(' 2.6 '), 3);
  for (const bad of ['', '  ', 'abc', 'NaN', 'Infinity', null, undefined]) assert.equal(parseBar(bad), null);
});
test('an unusable value keeps the range', () => {
  assert.deepEqual(editRange('from', '', cur, 8), cur); assert.deepEqual(editRange('to', 'x', cur, 8), cur);
});
test('values are clamped to 1..bars', () => {
  assert.deepEqual(editRange('from', '0', cur, 8), { from: 1, to: 4 });
  assert.deepEqual(editRange('to', '99', cur, 8), { from: 2, to: 8 });
  assert.deepEqual(editRange('from', '-5', cur, 8), { from: 1, to: 4 });
});
test('the edited field wins when first would pass last', () => {
  assert.deepEqual(editRange('from', '6', cur, 8), { from: 6, to: 6 });
  assert.deepEqual(editRange('to', '1', cur, 8), { from: 1, to: 1 });
});
test('a shorter take pulls a stale range back inside it', () => {
  assert.deepEqual(editRange('from', '', { from: 7, to: 9 }, 3), { from: 3, to: 3 });
  assert.deepEqual(editRange('to', '2', { from: 1, to: 1 }, 1), { from: 1, to: 1 });
});
