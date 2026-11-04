import test from 'node:test';
import assert from 'node:assert/strict';
import { distortionAudio } from '../clientside-distortion.mjs';

test('distortion mix zero is dry and hard clipping bounds the wet output', async () => {
  const audio = { sampleRate: 12000, channels: [Float32Array.of(0.8, -0.8, 0.4)] };
  const dry = await distortionAudio(audio, { drive: '36', mode: 'hard', mix: '0%' }, new AbortController().signal);
  assert.deepEqual([...dry.channels[0]], [...audio.channels[0]]);
  const wet = await distortionAudio(audio, { drive: '36', mode: 'hard', tone: '5000 Hz', mix: '100%' }, new AbortController().signal);
  assert.ok([...wet.channels[0]].every((value) => value >= -1 && value <= 1));
  assert.notDeepEqual([...wet.channels[0]], [...audio.channels[0]]);
});

test('distortion bypass preserves the original and checks cancellation', async () => {
  const audio = { sampleRate: 12000, channels: [Float32Array.of(0.2)] };
  assert.equal(await distortionAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => distortionAudio(audio, {}, controller.signal), { name: 'AbortError' });
});
