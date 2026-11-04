import assert from 'node:assert/strict';
import test from 'node:test';
import { humanizeMidiEvents } from '../clientside-midi-humanize.mjs';

test('humanize is deterministic from the exact effective seed and stays within ranges', () => {
  const input = { notes: Array.from({ length: 8 }, (_, index) => ({ bar: 1, beat: index / 4, pitch: 60, velocity: 96, tick: index })) };
  const params = { velocity: '10', timing: '20' }, first = humanizeMidiEvents(input, params, '1739377557');
  assert.deepEqual(humanizeMidiEvents(input, params, '1739377557'), first);
  assert.ok(first.notes.every((note, index) => Math.abs(note.velocity - 96) <= 10 && Math.abs(note.beat - input.notes[index].beat) <= 0.02 && !('tick' in note)));
  assert.notDeepEqual(humanizeMidiEvents(input, params, '1739377558'), first);
});

test('humanize bypass preserves the notes and metadata', () => {
  const input = { marker: 1, notes: [{ beat: 0.5, velocity: 90, tick: 12 }] };
  assert.deepEqual(humanizeMidiEvents(input, { enabled: 'off', velocity: '10', timing: '20' }, '1'), input);
});
