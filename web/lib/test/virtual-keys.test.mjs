import test from 'node:test';
import assert from 'node:assert/strict';
import { isBlack, keyLayout, pitchName, rangeFor, snapTicks, splitAtBars, velocityAt } from '../virtual-keys.mjs';

test('the layout counts white keys and puts black keys after the white key before them', () => {
  const { keys, whites } = keyLayout(60, 72);
  assert.equal(keys.length, 13);
  assert.equal(whites, 8);
  assert.deepEqual(keys.filter((k) => k.black).map((k) => k.pitch), [61, 63, 66, 68, 70]);
  assert.equal(keys.find((k) => k.pitch === 61).white, 0);   // C# sits after the first white key
  assert.equal(keys.find((k) => k.pitch === 64).white, 2);
  assert.ok(isBlack(70) && !isBlack(71));
  assert.equal(pitchName(60), 'C4');
  assert.equal(pitchName(70), 'A#4');
});

test('the range follows the octave and stays inside the MIDI range', () => {
  assert.deepEqual(rangeFor(3, 2), { first: 48, last: 72 });
  assert.deepEqual(rangeFor(-3, 2), { first: 0, last: 24 });
  assert.equal(rangeFor(9, 2).last, 127);
});

test('a touch low on the key is louder, fixed is always 100', () => {
  assert.ok(velocityAt(0.9) > velocityAt(0.1));
  assert.equal(velocityAt(0), 30);
  assert.equal(velocityAt(1), 127);
  assert.equal(velocityAt(0.5, 'fixed'), 100);
});

test('a played note is cut at the bar lines and snapped on the grid', () => {
  assert.equal(snapTicks(130, 120), 120);
  assert.equal(snapTicks(130, 0), 130);
  const pieces = splitAtBars(1800, 300, 60, 90, 480);   // a bar is 1920 ticks: 120 ticks in the first bar, 180 in the next
  assert.deepEqual(pieces.map((p) => [p.at, p.length]), [[1800, 120], [1920, 180]]);
  assert.equal(splitAtBars(0, 100, 60, 90, 480).length, 1);
  assert.ok(splitAtBars(0, 0, 60, 90, 480)[0].length >= 1);
});

import { barAt, selectRows, takeBars } from '../virtual-keys.mjs';
test('a take is measured in bars and a bar range of it becomes a clip of its own', () => {
  const ppq = 480, bar = 4 * ppq;
  const notes = [{ at: 0, length: 480, pitch: 60, velocity: 90 }, { at: bar + 100, length: 3 * bar, pitch: 64, velocity: 90 }, { at: 5 * bar, length: 100, pitch: 67, velocity: 90 }];
  assert.equal(takeBars(notes, ppq), 6);
  assert.equal(takeBars([], ppq), 1);
  const slice = selectRows(notes, 1, 3, ppq);   // bars 2 and 3 of the take
  assert.equal(slice.bars, 2);
  assert.deepEqual(slice.rows.map((r) => [r.pitch, r.at, r.length]), [[64, 100, 2 * bar - 100]]);   // starts inside, shortened at the end of the range
  assert.equal(selectRows(notes, 0, 6, ppq).rows.length, 3);
  assert.equal(barAt(0, 600, 6), 0);
  assert.equal(barAt(599, 600, 6), 5);
  assert.equal(barAt(300, 600, 6), 3);
});

import { SCALES, bendValue, scaleSet, snapToScale, takeToItems } from '../virtual-keys.mjs';
test('a scale lock moves a key to the nearest note of the scale', () => {
  const cMajor = scaleSet(0, 'major');
  assert.equal(snapToScale(60, cMajor), 60);          // C stays
  assert.equal(snapToScale(61, cMajor), 60);          // C# is as near to C as to D: down
  assert.equal(snapToScale(66, cMajor), 65);          // F# -> F
  assert.equal(snapToScale(70, cMajor), 69);          // A# -> A
  assert.equal(snapToScale(61, null), 61);            // no lock
  const aMinorPent = scaleSet(9, 'minor pentatonic'); // A C D E G
  assert.deepEqual([...aMinorPent].sort((a, b) => a - b), [0, 2, 4, 7, 9]);
  assert.equal(snapToScale(62 + 1, aMinorPent), 62);   // D# -> D (down on the tie)
  assert.equal(scaleSet(0, 'nope'), null);
  assert.ok(SCALES.blues.length === 6);
});
test('pitch bend bytes become -1 to 1', () => {
  assert.equal(bendValue(0, 64), 0);
  assert.equal(bendValue(0, 0), -1);
  assert.ok(bendValue(127, 127) > 0.999);
});
test('an ad-hoc take becomes notes on the grid', () => {
  const items = takeToItems([{ at: 0.52, end: 0.98, pitch: 60, velocity: 90 }, { at: 1.0, end: 1.2, pitch: 64, velocity: 80, bend: 200 }], 120, 480, 0.25);   // 120 bpm: a beat is 0.5 s; a sixteenth is 120 ticks
  assert.deepEqual(items.map((i) => [i.at, i.length]), [[480, 480], [960, 240]]);
  assert.equal(items[1].bend, 200);
});

import { snapRange, whiteKeys as whiteKeysOf } from '../virtual-keys.mjs';
test('the keys shown begin and end on a white key, and are between one and six octaves', () => {
  assert.deepEqual(snapRange(60, 84), { first: 60, last: 84 });
  const sharp = snapRange(61, 82);                     // C# and A# are black: the range moves out to white keys
  assert.ok(!isBlack(sharp.first) && !isBlack(sharp.last) && sharp.first <= 61 && sharp.last >= 82 - 1);
  assert.ok(whiteKeysOf(snapRange(60, 62).first, snapRange(60, 62).last) >= 5, 'at least five white keys');
  assert.ok(snapRange(0, 127).last - snapRange(0, 127).first <= 73, 'at most six octaves');
  assert.deepEqual(snapRange(84, 60), { first: 60, last: 84 });   // the wrong way round
  assert.ok(snapRange(-5, 300).first >= 0 && snapRange(-5, 300).last <= 127);
});

import { moveRange, whiteKeys } from '../virtual-keys.mjs';
test('moving the keys keeps the number of white keys and begins on a white key', () => {
  const whites = whiteKeys(48, 72);
  for (const delta of [-30, -7, -1, 1, 5, 13, 40]) {
    const moved = moveRange(48, 72, delta);
    assert.equal(whiteKeys(moved.first, moved.last), whites, 'delta ' + delta);
    assert.ok(!isBlack(moved.first) && !isBlack(moved.last));
  }
  const top = moveRange(48, 72, 200);
  assert.ok(top.last <= 127 && whiteKeys(top.first, top.last) === whites);
  const bottom = moveRange(48, 72, -200);
  assert.ok(bottom.first >= 0 && whiteKeys(bottom.first, bottom.last) === whites);
});
