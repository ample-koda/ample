import test from 'node:test';
import assert from 'node:assert/strict';
import { limiterAudio } from '../clientside-limiter.mjs';

test('limiter preserves bypass and applies linked lookahead gain to stereo peaks', async () => {
  const left = new Float32Array(500), right = new Float32Array(500);
  left[100] = 0.95; right[100] = 0.45; right[300] = 0.8;
  const audio = { sampleRate: 1000, channels: [left, right] };
  assert.equal(await limiterAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const output = await limiterAudio(audio, { ceiling: '-6 dB', lookahead: '5 ms', release: '100 ms' }, new AbortController().signal);
  assert.equal(output.channels.length, 2);
  assert.equal(output.channels[0].length, left.length);
  assert.ok(Math.abs(output.channels[0][100]) < Math.abs(left[100]));
  assert.ok(Math.abs(output.channels[1][100]) < Math.abs(right[100]));
  assert.ok(output.channels.every((channel) => channel.every(Number.isFinite)));
});

test('limiter checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => limiterAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, controller.signal), { name: 'AbortError' });
});
