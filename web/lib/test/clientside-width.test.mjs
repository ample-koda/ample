import test from 'node:test';
import assert from 'node:assert/strict';
import { widthAudio } from '../clientside-width.mjs';

test('width preserves the center image, folds to mono and leaves mono input unchanged', async () => {
  const signal = new AbortController().signal, audio = { sampleRate: 1, channels: [Float32Array.of(1), Float32Array.of(-1)] };
  const center = await widthAudio(audio, { width: '100' }, signal), mono = await widthAudio(audio, { width: '0' }, signal);
  assert.deepEqual([...center.channels[0]], [...audio.channels[0]]);
  assert.deepEqual([...center.channels[1]], [...audio.channels[1]]);
  assert.deepEqual([...mono.channels[0]], [0]);
  assert.deepEqual([...mono.channels[1]], [0]);
  const single = { sampleRate: 1, channels: [Float32Array.of(0.5)] };
  assert.equal(await widthAudio(single, { width: '200' }, signal), single);
});
