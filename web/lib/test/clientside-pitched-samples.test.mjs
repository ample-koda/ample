import test from 'node:test';
import assert from 'node:assert/strict';
import { pitchedSampleNotes, pitchedSampleWorkingSetBytes } from '../clientside-pitched-samples.mjs';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-pitched-samples-worker.js');

test('sample-folder preview accepts one plain note-named WAV per pitch only', () => {
  assert.deepEqual([...pitchedSampleNotes([{ name: 'c4.wav' }, { name: 'f#3.wav' }]).keys()], [60, 54]);
  assert.equal(pitchedSampleNotes([{ name: 'c4.wav' }, { name: 'c4_r2.wav' }]), null);
  assert.equal(pitchedSampleNotes([{ name: 'c4.wav' }, { name: 'C4.wav' }]), null);
});

test('sample preview working-set estimate includes input and output buffers', () => {
  assert.equal(pitchedSampleWorkingSetBytes(100, 200), 2400);
});

test('sample worker renders piano note timing and bass one-octave-down pulses', () => {
  const sample = new Float32Array(400).fill(0.2);
  const base = { samples: [{ pitch: 60, sourceRate: 8000, data: sample.buffer }], notes: [{ bar: 1, beat: 1, beats: 1, pitch: 60, velocity: 96 }], gainDb: 0, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 };
  self.onmessage({ data: { ...base, mode: 'piano' } });
  const piano = new Float32Array(self.message.channel);
  assert.equal(piano[1999], 0);
  assert.ok(piano[2000] > 0.05);
  self.onmessage({ data: { ...base, mode: 'bass' } });
  const bass = new Float32Array(self.message.channel);
  assert.ok(bass[0] > 0.05);
  assert.equal(bass[1000], 0);
  assert.ok(bass[2000] > 0.05);
});
