import test from 'node:test';
import assert from 'node:assert/strict';
import { transposeMidiEvents } from '../clientside-midi-transpose.mjs';

test('transpose clamps semitone range and MIDI pitch without changing note metadata', () => {
  const input = { notes: [{ pitch: 2, beat: 0.5, beats: 1, velocity: 90 }, { pitch: 126, bar: 2 }], marker: 'kept' };
  assert.deepEqual(transposeMidiEvents(input, { semitones: '-48' }), { notes: [{ pitch: 0, beat: 0.5, beats: 1, velocity: 90 }, { pitch: 78, bar: 2 }], marker: 'kept' });
  assert.deepEqual(transposeMidiEvents(input, { semitones: '99' }).notes.map((note) => note.pitch), [50, 127]);
  assert.deepEqual(transposeMidiEvents(input, { enabled: 'off', semitones: '12' }), input);
});
