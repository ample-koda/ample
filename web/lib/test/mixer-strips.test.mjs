import test from 'node:test';
import assert from 'node:assert/strict';
import { laneWords, stripCount } from '../mixer-strips.mjs';

test('the strips follow the inputs of the block, between 2 and 16 (issue 162)', () => {
  assert.equal(stripCount({ inputs: '8' }), 8);
  assert.equal(stripCount({ inputs: '2' }), 2);
  assert.equal(stripCount({}), 4);
  assert.equal(stripCount({ inputs: '40' }), 16);
  assert.equal(stripCount({ inputs: '1' }), 2);
});

test('more words than declared inputs show all of them', () => {
  assert.equal(stripCount({ inputs: '4', gains: '"0 0 0 0 0 0"' }), 6);
  assert.equal(stripCount({ inputs: '4', pan: 'c l r c c' }), 5);
});

test('a list is padded to the lanes and nothing is lost', () => {
  assert.deepEqual(laneWords('"-3 0"', 4, '0'), ['-3', '0', '0', '0']);
  assert.deepEqual(laneWords('c l r', 2, 'c'), ['c', 'l', 'r']);
  assert.deepEqual(laneWords(undefined, 3, 'c'), ['c', 'c', 'c']);
});
