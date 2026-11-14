import test from 'node:test';
import assert from 'node:assert/strict';
import { seekByKey } from '../seek-keys.mjs';

test('Left and Right seek 2 seconds, with Shift 10 (issue 221)', () => {
  assert.equal(seekByKey(20, 60, 'ArrowRight'), 22);
  assert.equal(seekByKey(20, 60, 'ArrowLeft'), 18);
  assert.equal(seekByKey(20, 60, 'ArrowRight', true), 30);
});

test('the position stays inside the sound, and Home and End go to the ends', () => {
  assert.equal(seekByKey(1, 60, 'ArrowLeft'), 0);
  assert.equal(seekByKey(59, 60, 'ArrowRight'), 60);
  assert.equal(seekByKey(20, 60, 'Home'), 0);
  assert.equal(seekByKey(20, 60, 'End'), 60);
});

test('another key does not seek', () => {
  assert.equal(seekByKey(20, 60, 'a'), null);
  assert.equal(seekByKey(20, 60, 'Enter'), null);
});
