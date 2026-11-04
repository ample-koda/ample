import test from 'node:test';
import assert from 'node:assert/strict';
import { flangerAudio } from '../clientside-flanger.mjs';

test('flanger combines modulated delay and feedback with wet/dry controls', async () => {
  const audio = { sampleRate: 1000, channels: [Float32Array.of(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)] };
  const dry = await flangerAudio(audio, { mix: '0%' }, new AbortController().signal);
  assert.deepEqual([...dry.channels[0]], [...audio.channels[0]]);
  const wet = await flangerAudio(audio, { depth: '1 ms', period: '100 ms', feedback: '80%', mix: '100%' }, new AbortController().signal);
  assert.ok(wet.channels[0].some((value, index) => index > 0 && value !== 0));
  assert.ok(wet.channels[0].length === audio.channels[0].length);
  assert.equal(await flangerAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
});

test('flanger checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => flangerAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, controller.signal), { name: 'AbortError' });
});
