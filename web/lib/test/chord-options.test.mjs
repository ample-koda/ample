import { test } from 'node:test';
import assert from 'node:assert/strict';
import { chordPitches } from '../chords.mjs';

test('chord stamp options change voicing without mutating the default', () => {
  assert.deepEqual(chordPitches(60, 'maj7'), [60, 64, 67, 71]);
  assert.deepEqual(chordPitches(60, 'maj7', { inversion: 1 }), [64, 67, 71, 72]);
  assert.deepEqual(chordPitches(60, 'maj7', { spread: true }), [60, 67, 76, 83]);
  assert.deepEqual(chordPitches(60, 'maj7', { drop2: true }), [55, 60, 64, 71]);
  assert.deepEqual(chordPitches(60, 'major', { bassNote: 48, octaveDouble: true }), [48, 60, 64, 67, 72]);
});
