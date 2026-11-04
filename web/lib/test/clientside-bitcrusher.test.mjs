import test from 'node:test';
import assert from 'node:assert/strict';
import { bitcrusherAudio } from '../clientside-bitcrusher.mjs';

test('bitcrusher reduces bit depth and holds samples while mix zero stays dry', async () => {
  const audio = { sampleRate: 8, channels: [Float32Array.of(0.8, 0.22, 0.33, 0.9)] };
  const held = await bitcrusherAudio(audio, { bits: '2', downsample: '2', mix: '100%' }, new AbortController().signal);
  assert.equal(held.channels[0][1], held.channels[0][0]);
  assert.equal(held.channels[0][3], held.channels[0][2]);
  assert.notEqual(held.channels[0][0], held.channels[0][2]);
  const dry = await bitcrusherAudio(audio, { bits: '2', downsample: '2', mix: '0%' }, new AbortController().signal);
  assert.deepEqual([...dry.channels[0]], [...audio.channels[0]]);
});

test('bitcrusher bypass preserves source and cancellation is honored', async () => {
  const audio = { sampleRate: 8, channels: [Float32Array.of(0.25)] };
  assert.equal(await bitcrusherAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => bitcrusherAudio(audio, {}, controller.signal), { name: 'AbortError' });
});
