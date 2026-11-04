import test from 'node:test';
import assert from 'node:assert/strict';

const self = globalThis.self = { postMessage: (message) => { self.message = message; } };
await import('../clientside-stretch-worker.js');

test('local stretch preserves duration and tone pitch at double speed', () => {
  const rate = 24000, input = new Int16Array(rate);
  for (let i = 0; i < input.length; i++) input[i] = Math.round(Math.sin(2 * Math.PI * 440 * i / rate) * 20000);
  self.onmessage({ data: { channels: [input.buffer], speed: 200, sampleRate: rate } });
  const output = new Float32Array(self.message.channels[0]);
  assert.equal(output.length, input.length);
  let crossings = 0;
  for (let i = 1201; i < 10800; i++) if (output[i - 1] <= 0 && output[i] > 0) crossings++;
  const hz = crossings / ((10800 - 1200) / rate);
  assert.ok(Math.abs(hz - 440) < 20, `expected about 440 Hz, got ${hz}`);
});

test('automated stretch remains on the server path', async () => {
  const { processStretchChannels, stretchWorkingSetBytes } = await import('../clientside-stretch.mjs');
  await assert.rejects(processStretchChannels([new Float32Array(8)], 24000, { speed: 'auto(1:100, 3:50)' }), /server renderer/);
  assert.ok(stretchWorkingSetBytes(48000, 2) > stretchWorkingSetBytes(24000, 2));
});
