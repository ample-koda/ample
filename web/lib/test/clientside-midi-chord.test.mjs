import test from 'node:test';
import assert from 'node:assert/strict';
import { chordMidiEvents } from '../clientside-midi-chord.mjs';

test('chord maps types and inversions while preserving each note event', () => {
  const input = { notes: [{ pitch: 60, bar: 2, beat: 0.5, beats: 1, velocity: 80 }] };
  assert.deepEqual(chordMidiEvents(input, { type: 'minor', inversion: '0' }).notes.map((note) => note.pitch), [60, 63, 67]);
  assert.deepEqual(chordMidiEvents(input, { type: 'major', inversion: '1' }).notes, [
    { pitch: 72, bar: 2, beat: 0.5, beats: 1, velocity: 80 },
    { pitch: 64, bar: 2, beat: 0.5, beats: 1, velocity: 80 },
    { pitch: 67, bar: 2, beat: 0.5, beats: 1, velocity: 80 },
  ]);
  assert.deepEqual(chordMidiEvents({ notes: [{ pitch: 126 }] }, { type: 'power', inversion: '0' }).notes.map((note) => note.pitch), [126, 127, 127]);
  assert.deepEqual(chordMidiEvents(input, { enabled: 'off' }), input);
});
