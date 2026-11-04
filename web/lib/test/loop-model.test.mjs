import test from 'node:test';
import assert from 'node:assert/strict';
import { barAt, barOfClick, barSeconds, positionAt, rangeFromDrag, rangeSeconds, untilNextReturn } from '../loop-model.mjs';

test('a bar lasts four beats of the tempo, and bars and seconds convert both ways', () => {
  assert.equal(barSeconds(120), 2);
  assert.equal(barSeconds(60), 4);
  assert.equal(barSeconds(92).toFixed(4), '2.6087');
  assert.equal(barAt(0, 120), 1);
  assert.equal(barAt(5, 120), 3.5);
  assert.deepEqual(rangeSeconds({ at: 5, bars: 4 }, 120, 100), { start: 8, end: 16 });
  assert.deepEqual(rangeSeconds({ at: 5, bars: 4 }, 120, 12), { start: 8, end: 12 });   // kept inside the take
  assert.equal(rangeSeconds({ at: 50, bars: 4 }, 120, 12), null);   // beyond the end
  assert.equal(rangeSeconds(null, 120, 100), null);
});

test('the playhead runs to the end of the loop and starts again at its start', () => {
  const loop = { start: 8, end: 16 };
  assert.equal(positionAt(0, 8, loop, 100), 8);
  assert.equal(positionAt(3, 8, loop, 100), 11);
  assert.equal(positionAt(8, 8, loop, 100), 8);   // exactly back at the start
  assert.equal(positionAt(11, 8, loop, 100), 11);
  assert.equal(positionAt(3, 14, loop, 100), 9, 'started 2 s before the end: after 3 s it is 1 s into the loop');
  assert.equal(positionAt(1, 0, loop, 100), 1, 'before the loop it just plays');
  assert.equal(positionAt(30, 0, null, 20), 20, 'without a loop it stops at the end of the take');
});

test('the swap waits for the loop to come round', () => {
  const loop = { start: 8, end: 16 };
  assert.equal(untilNextReturn(0, 8, loop), 8);
  assert.equal(untilNextReturn(5, 8, loop), 3);
  assert.equal(untilNextReturn(8, 8, loop), 8);   // just came round: a whole loop again
  assert.equal(untilNextReturn(10, 8, loop), 6);
  assert.equal(untilNextReturn(1, 14, loop), 1, 'one second to go');
  assert.equal(untilNextReturn(3, 0, null), 0);
});

test('a drag on the ruler chooses whole bars, a click does not', () => {
  assert.deepEqual(rangeFromDrag(10, 130, 30), { at: 1, bars: 5 });
  assert.deepEqual(rangeFromDrag(130, 10, 30), { at: 1, bars: 5 });
  assert.deepEqual(rangeFromDrag(35, 40, 30, 3), { at: 2, bars: 1 });
  assert.equal(rangeFromDrag(35, 38, 30), null);
  assert.equal(barOfClick(0, 30), 1);
  assert.equal(barOfClick(75, 30), 3.5);
  assert.equal(barOfClick(-20, 30), 1);
});
