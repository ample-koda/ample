import test from 'node:test';
import assert from 'node:assert/strict';
import { delayAudio } from '../clientside-delay.mjs';

test('delay spaces feedback echoes by the requested milliseconds and keeps the input duration', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(1, 0, 0, 0, 0)] };
  const result = await delayAudio(audio, { time: '250 ms', feedback: '50%', mix: '100%' }, new AbortController().signal);
  assert.deepEqual([...result.channels[0]], [1, 1, 0.5, 0.25, 0.125]);
  assert.equal(result.channels[0].length, audio.channels[0].length);
});

test('delay bypass returns its source and abort is checked between processing chunks', async () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.2)] };
  assert.equal(await delayAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => delayAudio(audio, { time: '1ms' }, controller.signal), { name: 'AbortError' });
});
