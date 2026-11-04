import test from 'node:test';
import assert from 'node:assert/strict';
import { duckAudio } from '../clientside-duck.mjs';

test('duck applies the pattern at tempo with a linked channel gain', async () => {
  const audio = { sampleRate: 160, channels: [new Float32Array(640).fill(0.8), new Float32Array(640).fill(0.4)] };
  const result = await duckAudio(audio, { depth: '20 dB', release: '100 ms', pattern: 'x.x.' }, 60, new AbortController().signal);
  assert.ok(result.channels[0][1] < audio.channels[0][1]);
  assert.ok(Math.abs(result.channels[0][1] / result.channels[1][1] - 2) < 1e-6);
  assert.ok(result.channels[0][81] < audio.channels[0][81]);
  assert.equal(result.channels[0][97], audio.channels[0][97]);
});

test('duck bypass preserves input and abort is honored', async () => {
  const audio = { sampleRate: 100, channels: [Float32Array.of(0.5)] };
  assert.equal(await duckAudio(audio, { enabled: 'off' }, 120, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => duckAudio(audio, {}, 120, controller.signal), { name: 'AbortError' });
});
