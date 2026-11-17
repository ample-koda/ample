import test from 'node:test';
import assert from 'node:assert/strict';
import { padChannels } from '../live-pad.mjs';

test('a shorter sound is followed by silence up to the loop length', () => {
  const out = padChannels([Float32Array.of(1, 2, 3), Float32Array.of(4, 5, 6)], 5);
  assert.deepEqual([...out[0]], [1, 2, 3, 0, 0]); assert.deepEqual([...out[1]], [4, 5, 6, 0, 0]);
});
test('a sound that is long enough is not copied or cut', () => {
  const channels = [Float32Array.of(1, 2, 3)];
  assert.equal(padChannels(channels, 3), channels); assert.equal(padChannels(channels, 2), channels);
});
