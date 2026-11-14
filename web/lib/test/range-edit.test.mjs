import test from 'node:test';
import assert from 'node:assert/strict';
import { rangeFromFields } from '../range-edit.mjs';

test('two fields make a range of bars to audition (issue 257)', () => {
  assert.deepEqual(rangeFromFields('9', '2'), { at: 9, bars: 2 });
  assert.deepEqual(rangeFromFields(' 3 ', '1'), { at: 3, bars: 1 });
});

test('a fraction is rounded, and the range stays inside the limit', () => {
  assert.deepEqual(rangeFromFields('2.4', '1.6'), { at: 2, bars: 2 });
  assert.deepEqual(rangeFromFields('998', '10', 999), { at: 998, bars: 2 });
});

test('fields that make no range give null', () => {
  assert.equal(rangeFromFields('', '2'), null);
  assert.equal(rangeFromFields('0', '2'), null);
  assert.equal(rangeFromFields('3', '0'), null);
  assert.equal(rangeFromFields('x', '2'), null);
  assert.equal(rangeFromFields('1000', '1', 999), null);
});
