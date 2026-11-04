import test from 'node:test';
import assert from 'node:assert/strict';
import { phaserAudio } from '../clientside-phaser.mjs';

test('phaser respects bypass and dry/wet endpoints while preserving stereo shape', async () => {
  const audio = { sampleRate: 1000, channels: [Float32Array.from({ length: 400 }, (_, i) => Math.sin(i / 9)), Float32Array.from({ length: 400 }, (_, i) => Math.cos(i / 11))] };
  assert.equal(await phaserAudio(audio, { enabled: 'off' }, new AbortController().signal), audio);
  const dry = await phaserAudio(audio, { mix: '0%' }, new AbortController().signal);
  assert.deepEqual([...dry.channels[0]], [...audio.channels[0]]);
  const wet = await phaserAudio(audio, { stages: '8', period: '100 ms', mix: '100%' }, new AbortController().signal);
  assert.equal(wet.channels.length, 2);
  assert.equal(wet.channels[0].length, audio.channels[0].length);
  assert.ok(wet.channels[0].some((value, i) => Math.abs(value - audio.channels[0][i]) > 1e-4));
  assert.ok(wet.channels[0].every(Number.isFinite));
});

test('phaser checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => phaserAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, controller.signal), { name: 'AbortError' });
});
