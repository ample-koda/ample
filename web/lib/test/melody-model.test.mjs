import test from 'node:test';
import assert from 'node:assert/strict';
import { chordClasses, effectiveVelocity, overlaps, parseChords, parseMelody, room, roman, serializeChords, serializeMelody } from '../melody-model.mjs';

test('a melody round-trips: notes, rests, velocities and dynamics words', () => {
  for (const text of ['e5:0.5 r:1.5 g5:0.5 r:1.5 c5:4', 'mp c4:1 d4:1 < e4:2:60 g4:1 ff c5:4', 'c#4:1 bb3:0.25 r:2']) {
    const m = parseMelody(`"${text}"`);
    assert.ok(m.ok, text);
    assert.equal(serializeMelody(m, { trailingRest: m.trailingRest }), text.replace(/^bb3/, 'a#3').replace(' bb3', ' a#3'));
  }
});

test('a word that is not understood keeps the editor out', () => {
  const m = parseMelody('c4:1 wobble d4:1');
  assert.equal(m.ok, false);
  assert.match(m.reasons[0], /wobble/);
});

test('velocity: own value, else the last level word, else 96', () => {
  const m = parseMelody('mp c4:1 d4:1:100 ff e4:1');
  assert.deepEqual(m.notes.map((n) => effectiveVelocity(n, m.marks)), [64, 100, 112]);
  assert.equal(effectiveVelocity({ at: 0, vel: null }, []), 96);
});

test('overlap and room', () => {
  const notes = [{ at: 1, len: 1 }, { at: 3, len: 1 }];
  assert.equal(overlaps(notes, 0, 1), false);
  assert.equal(overlaps(notes, 0.5, 1), true);
  assert.equal(room(notes, 1.5, 8), 1.5);
});

test('chords: bars, dots, inversions, keyboard notes and Roman numerals', () => {
  const bars = parseChords('Dm Dm Bb.F C/E Gm7 A');
  assert.equal(serializeChords(bars), 'Dm Dm Bb.F C/E Gm7 A');
  assert.equal(bars.length, 5 + 1);
  assert.deepEqual(chordClasses(bars[3][0]), [4, 0, 7]);
  assert.equal(roman(bars[0][0], 2, true), 'i');
  assert.equal(roman(bars[5][0], 2, true), 'V');
});

test('a chord word that is not understood is kept as it is', () => {
  assert.equal(serializeChords(parseChords('Dm X9z G')), 'Dm X9z G');
});
