import test from 'node:test';
import assert from 'node:assert/strict';
import { regionAt, positionIn } from '../loop-region.mjs';

test('the region snaps to the bar line below the playhead and stays inside the take', () => {
  assert.deepEqual(regionAt(5.3, 2, 2, 16).start, 4);
  assert.equal(regionAt(5.3, 2, 2, 16).length, 4);
  assert.equal(regionAt(15, 2, 4, 16).start, 8);   // would run past the end, so it starts earlier
  assert.equal(regionAt(1, 2, 8, 6).length, 6);    // longer than the take: the whole take
});

test('the position wraps inside the region with the expected period', () => {
  const r = regionAt(5.3, 2, 2, 16, 10);   // start 4, length 4, playhead 5.3 at t=10
  assert.ok(Math.abs(positionIn(r, 10, 0, 16) - 5.3) < 1e-9);
  assert.ok(Math.abs(positionIn(r, 12.7, 0, 16) - 8.0) < 1e-9 || Math.abs(positionIn(r, 12.7, 0, 16) - 4.0) < 1e-9);
  assert.ok(Math.abs(positionIn(r, 14, 0, 16) - positionIn(r, 10, 0, 16)) < 1e-9);   // one period later
});

test('without a region the whole take loops from t0', () => {
  assert.ok(Math.abs(positionIn(null, 21, 1, 8) - 4) < 1e-9);
});
