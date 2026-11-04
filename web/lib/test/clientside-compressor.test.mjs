import test from 'node:test';
import assert from 'node:assert/strict';
import { compressorAudio } from '../clientside-compressor.mjs';

test('compressor bypasses cleanly and reduces above-threshold levels with linked channels', async () => {
  const left = Float32Array.from({ length: 2000 }, (_, i) => i > 300 && i < 1200 ? 0.8 : 0.05), right = Float32Array.from(left, (value) => value * 0.5);
  const audio = { sampleRate: 1000, channels: [left, right] };
  assert.equal(await compressorAudio(audio, { enabled: 'off' }, null, new AbortController().signal), audio);
  const output = await compressorAudio(audio, { threshold: '-24 dB', ratio: '8', attack: '5 ms', release: '40 ms', makeup: '3 dB' }, null, new AbortController().signal);
  assert.equal(output.channels.length, 2);
  assert.ok(Math.abs(output.channels[0][700]) < Math.abs(left[700]));
  assert.ok(Math.abs(output.channels[1][700] / output.channels[0][700] - 0.5) < 1e-5);
  assert.ok(output.channels.every((channel) => channel.every(Number.isFinite)));
});

test('sidechain key drives compression and keyFilter rejects low-only key content', async () => {
  const input = { sampleRate: 1000, channels: [new Float32Array(1000).fill(0.6)] };
  const key = { sampleRate: 1000, channels: [Float32Array.from({ length: 1000 }, (_, i) => i < 500 ? 0.8 : 0)] };
  const dry = await compressorAudio(input, { threshold: '-12 dB', attack: '1 ms', release: '20 ms' }, null, new AbortController().signal);
  const keyed = await compressorAudio(input, { threshold: '-12 dB', attack: '1 ms', release: '20 ms' }, key, new AbortController().signal);
  assert.ok(keyed.channels[0][250] < dry.channels[0][250]);
  const bass = { sampleRate: 1000, channels: [Float32Array.from({ length: 1000 }, (_, i) => Math.sin(i * 2 * Math.PI * 20 / 1000) * (i < 500 ? 0.8 : 0))] };
  const filtered = await compressorAudio(input, { threshold: '-12 dB', attack: '1 ms', release: '20 ms', keyFilter: '180 Hz' }, bass, new AbortController().signal);
  assert.ok(filtered.channels[0][250] > keyed.channels[0][250]);
});

test('compressor checks cancellation before processing', async () => {
  const controller = new AbortController(); controller.abort();
  await assert.rejects(() => compressorAudio({ sampleRate: 1000, channels: [new Float32Array(10)] }, {}, null, controller.signal), { name: 'AbortError' });
});
