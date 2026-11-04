import assert from 'node:assert/strict';
import test from 'node:test';
import { arpeggiateMidiEvents } from '../clientside-midi-arpeggiator.mjs';

test('arpeggiator orders, gates and expands chord pitches like the engine', () => {
  const notes = [{ bar: 1, beat: 0, beats: 1, pitch: 60, velocity: 90, ticks: 480 }, { bar: 1, beat: 0, beats: 1, pitch: 64, velocity: 92 }];
  const output = arpeggiateMidiEvents({ notes }, { rate: '1/8', pattern: 'updown', octaves: '2', gate: '50' }, '1').notes;
  assert.deepEqual(output.map((note) => [note.pitch, note.beat, note.beats]), [[60, 0, 0.25], [64, 0.5, 0.25]]);
  assert.ok(output.every((note) => !('tick' in note) && !('ticks' in note)));
});

test('random arpeggiator follows the exact seed and bypass passes through untouched', () => {
  const input = { notes: [{ bar: 1, beat: 0, beats: 2, pitch: 60 }, { bar: 1, beat: 0, beats: 2, pitch: 64 }] }, params = { rate: '1/8', pattern: 'random', octaves: '2', gate: '80' };
  assert.deepEqual(arpeggiateMidiEvents(input, params, '1739377557'), arpeggiateMidiEvents(input, params, '1739377557'));
  assert.notDeepEqual(arpeggiateMidiEvents(input, params, '1739377557'), arpeggiateMidiEvents(input, params, '1739377558'));
  assert.deepEqual(arpeggiateMidiEvents(input, { ...params, enabled: 'off' }, '1'), input);
});
