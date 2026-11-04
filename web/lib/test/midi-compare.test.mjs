import test from 'node:test';
import assert from 'node:assert/strict';
import { compareMidiNotes } from '../midi-compare.js';

test('matches generated MIDI note vectors and reports integer timing and velocity deltas', () => {
  const source = [{ bar: 1, pitch: 60, beat: 0, beats: 1, velocity: 90 }, { bar: 1, pitch: 64, beat: 2, beats: 1, velocity: 80 }];
  const converted = [{ bar: 1, pitch: 60, beat: 0.0125, beats: 1, velocity: 86 }, { bar: 1, pitch: 67, beat: 2, beats: 1, velocity: 80 }];
  assert.deepEqual(compareMidiNotes(source, converted, 480, 10), { found: 1, missing: 1, extra: 1, timing: { maxTicks: 6, meanTicks: 3 }, velocity: { max: 4, mean: 4 }, pairs: [{ onsetDeltaTicks: 6, durationDeltaTicks: 0, velocityDelta: -4 }] });
});

test('rejects note timing that cannot be represented exactly at the selected PPQ', () => {
  assert.throws(() => compareMidiNotes([{ bar: 1, pitch: 60, beat: 0.000001, beats: 1, velocity: 90 }], [], 24, 0), /not exact/);
});

test('compares exact PPQ tick fields without using rounded display beats', () => {
  const original = [{ bar: 1, pitch: 60, beat: 0.142857, beats: 0.142857, tick: 1, ticks: 1, ppq: 7, velocity: 90 }];
  const converted = [{ bar: 1, pitch: 60, beat: 0.142857, beats: 0.142857, tick: 1, ticks: 1, ppq: 7, velocity: 90 }];
  assert.equal(compareMidiNotes(original, converted, 7, 0).found, 1);
});
