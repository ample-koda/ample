import test from 'node:test';
import assert from 'node:assert/strict';
import { midiSourceEvents } from '../clientside-midi-source.mjs';

test('SMF note ticks become song bars and beats with placement offset', () => {
  const events = midiSourceEvents({ ppq: 480, notes: [
    { start: 2160, end: 3120, pitch: 64, velocity: 90 },
    { start: 240, end: 720, pitch: 60, velocity: 100 },
  ] }, [{ at: 3, length: 2 }], 8);
  assert.deepEqual(events, [
    { bar: 3, beat: 0.5, beats: 1, pitch: 60, velocity: 100, pan: 0 },
    { bar: 4, beat: 0.5, beats: 2, pitch: 64, velocity: 90, pan: 0 },
  ]);
});

test('SMF notes clip at the MIDI block length and repeat for each placement', () => {
  const events = midiSourceEvents({ ppq: 96, notes: [{ start: 0, end: 1200, pitch: 48, velocity: 80 }] }, [{ at: 2, length: 1 }, { at: 5, length: 2 }], 8);
  assert.deepEqual(events, [
    { bar: 2, beat: 0, beats: 4, pitch: 48, velocity: 80, pan: 0 },
    { bar: 5, beat: 0, beats: 8, pitch: 48, velocity: 80, pan: 0 },
  ]);
});

test('invalid, malformed or truncated MIDI note responses fall back safely', () => {
  assert.equal(midiSourceEvents({ ppq: 0, notes: [] }, [], 8), null);
  assert.equal(midiSourceEvents({ ppq: 480, truncated: true, notes: [] }, [], 8), null);
  assert.equal(midiSourceEvents({ ppq: 480, notes: [{ start: 4, end: 2, pitch: 61, velocity: 90 }] }, [], 8), null);
});
