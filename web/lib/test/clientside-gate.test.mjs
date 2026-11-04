import test from 'node:test';
import assert from 'node:assert/strict';
import { gateAudio } from '../clientside-gate.mjs';

test('gate bypasses cleanly and opens/closes linked channels around the threshold', async () => {
  const left = new Float32Array(2000), right = new Float32Array(2000);
  left.fill(0.6, 300, 900); right.fill(0.3, 300, 900);
  const audio = { sampleRate: 1000, channels: [left, right] };
  assert.equal(await gateAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const output = await gateAudio(audio, { threshold: '-20 dB', attack: '5 ms', hold: '80 ms', release: '100 ms' }, new AbortController().signal);
  assert.equal(output.channels.length, 2);
  assert.ok(output.channels[0][350] > 0.5);
  assert.ok(output.channels[0][1600] < 0.01);
  assert.ok(Math.abs(output.channels[1][350] / output.channels[0][350] - 0.5) < 1e-5);
});

test('gate checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => gateAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, controller.signal), { name: 'AbortError' });
});
