import test from 'node:test';
import assert from 'node:assert/strict';
import { guitarSampleNotes, guitarVoice } from '../clientside-guitar.mjs';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-guitar-worker.js');

test('guitar sample set accepts unique note names and rejects layers', () => {
  assert.deepEqual([...guitarSampleNotes([{ name: 'e2.wav' }, { name: 'a2.wav' }]).keys()], [40, 45]);
  assert.equal(guitarSampleNotes([{ name: 'e2.wav' }, { name: 'e2_v2.wav' }]), null);
  assert.equal(guitarSampleNotes([{ name: 'e2.wav' }, { name: 'E2.wav' }]), null);
});

test('guitar voicings match the supported open, triad and power shapes', () => {
  assert.deepEqual(guitarVoice([60, 64, 67], 'power'), [48, 55, 60]);
  assert.deepEqual(guitarVoice([60, 64, 67], 'triad'), [48, 52, 55]);
  assert.deepEqual(guitarVoice([60, 64, 67], 'open'), [48, 55, 60, 64, 67]);
});

test('guitar worker renders bounded sample audio for a plain downstroke', () => {
  const sample = new Float32Array(8000).fill(0.1), samples = [40, 47, 52, 56, 59].map((pitch) => ({ pitch, sourceRate: 8000, samples: sample.buffer.slice(0) }));
  self.onmessage({ data: { samples, notes: [{ bar: 1, beat: 0, pitch: 60 }], params: { voicing: 'open', stagger: 0, gain: 0 }, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000, strums: ['D'], activeBars: [1] } });
  const audio = new Float32Array(self.message.channels[0]);
  assert.equal(audio.length, 8000);
  assert.ok(Math.max(...audio.slice(0, 100)) > 0);
});
