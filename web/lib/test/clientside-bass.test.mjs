import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-bass-worker.js');

test('built-in bass makes four short notes at one octave below the lowest MIDI note', () => {
  self.onmessage({ data: { notes: [{ bar: 1, pitch: 48 }, { bar: 1, pitch: 55 }], sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const output = new Float32Array(self.message.channel);
  assert.ok(Math.max(...output.slice(0, 500)) > 0.1);
  assert.ok(Math.max(...output.slice(2100, 2500)) > 0.1);
  assert.equal(Math.max(...output.slice(8000)), -Infinity);
});

test('bass working-set estimate is bounded in proportion to duration', async () => {
  const { bassWorkingSetBytes } = await import('../clientside-bass.mjs');
  assert.equal(bassWorkingSetBytes(100), 800);
});
