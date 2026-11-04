import test from 'node:test';
import assert from 'node:assert/strict';
import { encodeVocalMidi, notesFromPitch } from '../vocal-midi.js';

test('groups generated vocal pitch frames into editable integer MIDI notes', () => {
  const notes = notesFromPitch([
    { t: 0, hz: 220, midi: 57, confidence: 0.9 },
    { t: 0.01, hz: 222, midi: 57.1, confidence: 0.8 },
    { t: 0.02, hz: 0, midi: 0, confidence: 0 },
    { t: 0.03, hz: 330, midi: 64, confidence: 0.4 },
    { t: 0.04, hz: 261, midi: 60, confidence: 0.95 },
  ], 0.01);
  assert.deepEqual(notes.map(({ startTick, endTick, pitch, velocity }) => ({ startTick, endTick, pitch, velocity })), [
    { startTick: 0, endTick: 19, pitch: 57, velocity: 108 },
    { startTick: 38, endTick: 48, pitch: 60, velocity: 116 },
  ]);
});

test('encodes reviewed notes as a deterministic format-0 Standard MIDI File', () => {
  const bytes = encodeVocalMidi([{ startTick: 0, endTick: 480, pitch: 60, velocity: 90 }]);
  assert.equal(new TextDecoder().decode(bytes.subarray(0, 4)), 'MThd');
  assert.equal(new TextDecoder().decode(bytes.subarray(14, 18)), 'MTrk');
  assert.equal(bytes.at(-3), 0xff);
  assert.deepEqual(encodeVocalMidi([{ startTick: 0, endTick: 480, pitch: 60, velocity: 90 }]), bytes);
  assert.throws(() => encodeVocalMidi([{ startTick: 5, endTick: 5, pitch: 60, velocity: 90 }]), /outside the MIDI range/);
});
