import test from 'node:test';
import assert from 'node:assert/strict';
import { barsFromStart, songBarAt, takeSecondsOf } from '../take-bars.mjs';

test('two bars into a take of bars 9-12 is song bar 11, not bar 3', () => {
  assert.equal(songBarAt(4, 2, 9), 11);   // 2 s per bar, 4 s in
  assert.equal(songBarAt(4, 2, 1), 3);    // a take of the whole song starts at bar 1
});

test('a song bar maps to the time in the take, and a bar outside the take to nothing', () => {
  assert.equal(takeSecondsOf(11, 2, 9, 8), 4);
  assert.equal(takeSecondsOf(9, 2, 9, 8), 0);
  assert.equal(takeSecondsOf(8, 2, 9, 8), null);    // before the first bar of the take
  assert.equal(takeSecondsOf(13, 2, 9, 8), null);   // after its last bar
  assert.equal(takeSecondsOf(3, 2, 1, 0), 4);       // an unknown length does not cut anything
});

test('the playhead of a range take is drawn at the song position', () => {
  assert.equal(barsFromStart(4, 2, 9), 10);
  assert.equal(barsFromStart(4, 2, 1), 2);
});
