import test from 'node:test';
import assert from 'node:assert/strict';
import { previewMidiNotes } from '../gm-audition.js';

test('GM audition excerpt keeps exact ticks and instrument routing and caps notes at 128', () => {
  const source = Array.from({ length: 140 }, (_, i) => `1:${i}:1:${48 + i % 24}:90:9:128:40:0`).join(' ');
  const notes = previewMidiNotes(source, 480, 128);
  assert.equal(notes.length, 128);
  assert.deepEqual(notes[0], { bar: 1, tick: 0, ticks: 1, ppq: 480, pitch: 48, velocity: 90, channel: 9, bank: 128, program: 40 });
  assert.equal(notes[127].tick, 127);
  assert.deepEqual(previewMidiNotes('1:0:480:60:90', 480, 128), [{ bar: 1, tick: 0, ticks: 480, ppq: 480, pitch: 60, velocity: 90 }]);
});

test('GM audition rejects invalid clips and unsupported excerpt bounds', () => {
  assert.deepEqual(previewMidiNotes('1:0:-1:60:90', 480, 128), []);
  assert.deepEqual(previewMidiNotes('1:0:120:60:90', 480, 129), []);
});
