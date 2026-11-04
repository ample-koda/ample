import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-sampler-worker.js');

test('sampler plays a sample at root pitch and transposes it by playback rate', () => {
  const sample = new Float32Array(8000);
  for (let i = 0; i < sample.length; i++) sample[i] = Math.sin(2 * Math.PI * 440 * i / 8000) * 0.5;
  self.onmessage({ data: { channels: [sample.buffer], sourceRate: 8000, notes: [{ bar: 1, beat: 0, beats: 1, pitch: 60, velocity: 127 }], params: { root: '60', mode: 'oneshot', gain: '0' }, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const output = new Float32Array(self.message.channels[0]);
  assert.ok(Math.abs(output[4] - sample[4]) < 0.001);
  assert.ok(Math.max(...output.slice(0, 1000)) > 0.4);
});

test('hold mode stops and fades at the note end', () => {
  const sample = new Float32Array(8000).fill(0.5);
  self.onmessage({ data: { channels: [sample.buffer], sourceRate: 8000, notes: [{ bar: 1, beat: 0, beats: 0.25, pitch: 60, velocity: 127 }], params: { root: '60', mode: 'hold', release: '10', gain: '0' }, sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const output = new Float32Array(self.message.channels[0]);
  assert.ok(output[450] < output[250]);
  assert.equal(output[600], 0);
});
