import assert from 'node:assert/strict';
import test from 'node:test';
import { quantizeMidiEvents } from '../clientside-midi-quantize.mjs';

test('quantize mirrors engine grid, strength, length and tick removal', () => {
  const input = { notes: [{ bar: 1, beat: 0.13, beats: 0.62, pitch: 60, tick: 12, ticks: 60 }] };
  assert.deepEqual(quantizeMidiEvents(input, { grid: '1/8', strength: '50', lengths: 'on' }).notes[0], { bar: 1, beat: 0.065, beats: 0.56, pitch: 60 });
  assert.equal(input.notes[0].tick, 12);
});

test('quantize handles triplet grid, half-step rounding and bypass', () => {
  assert.equal(quantizeMidiEvents({ notes: [{ beat: 0.166667 }] }, { grid: '1/8t', strength: '100' }).notes[0].beat, 0.333);
  const note = { beat: 0.125, tick: 7 };
  assert.deepEqual(quantizeMidiEvents({ notes: [note] }, { enabled: 'off', grid: '1/16' }).notes[0], note);
});
