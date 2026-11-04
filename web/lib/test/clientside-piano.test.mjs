import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-piano-worker.js');

test('built-in piano follows the note pitch and decays over note duration', () => {
  self.onmessage({ data: { notes: [{ bar: 1, beat: 0, beats: 1, pitch: 69 }], sampleRate: 8000, firstBar: 1, bars: 1, samplesPerBar: 8000 } });
  const output = new Float32Array(self.message.channel);
  let crossings = 0;
  for (let i = 100; i < 1900; i++) if (output[i - 1] <= 0 && output[i] > 0) crossings++;
  assert.ok(Math.abs(crossings - 99) < 12);
  assert.ok(Math.abs(output[1900]) < Math.abs(output[100]));
});

test('piano output duration sets the working-set budget', async () => {
  const { pianoWorkingSetBytes } = await import('../clientside-piano.mjs');
  assert.equal(pianoWorkingSetBytes(100), 800);
});
