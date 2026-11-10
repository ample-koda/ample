import test from 'node:test';
import assert from 'node:assert/strict';
import { notesIn, notesOf, thumbRects } from '../seq-notes.mjs';

test('a row counts its note entries and nothing else', () => {
  assert.equal(notesIn('1:0:1:60:96 1:1:0.5:64:80'), 2);
  assert.equal(notesIn(''), 0);
  assert.equal(notesIn('x...x...'), 0);
  assert.equal(notesIn('= 1:0:0.5:60:100 |> repeat(4)'), 0);   // a formula is worked out later, it has no notes of its own yet
  assert.deepEqual(notesOf('2:1.5:0.5:67:100'), [{ bar: 2, beat: 1.5, length: 0.5, pitch: 67, velocity: 100 }]);
});

test('the picture puts the highest note at the top and keeps every rectangle inside the pattern', () => {
  const rects = thumbRects('1:0:1:60:96 2:3:2:72:80', 2);
  assert.equal(rects.length, 2);
  assert.equal(rects[1].y, 0);
  assert.equal(rects[0].y, 1);
  for (const r of rects) { assert.ok(r.x >= 0 && r.x + r.w <= 1.0000001); assert.ok(r.w > 0); }
  assert.ok(Math.abs(rects[1].x - 7 / 8) < 1e-9);   // bar 2 beat 3 of 8 beats
});

test('one pitch sits in the middle and an empty row has no picture', () => {
  assert.equal(thumbRects('1:0:1:60:96 1:1:1:60:96', 1)[0].y, 0.5);
  assert.deepEqual(thumbRects('', 4), []);
});
