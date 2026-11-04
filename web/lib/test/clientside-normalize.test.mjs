import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeAudio } from '../clientside-normalize.mjs';

test('peak normalization reaches the target and respects the maximum gain', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(0.25, -0.5)] };
  const target = await normalizeAudio(audio, { mode: 'peak', peak: '-6 dBFS', max_gain: '48' }, new AbortController().signal);
  assert.ok(Math.abs(target.channels[0][1] + 10 ** (-6 / 20)) < 1e-6);
  const limited = await normalizeAudio(audio, { mode: 'peak', peak: '0', max_gain: '3' }, new AbortController().signal);
  assert.ok(Math.abs(limited.channels[0][1] + 0.5 * 10 ** (3 / 20)) < 1e-6);
});

test('silent input stays unchanged and loudness mode requests the server analysis path', async () => {
  const silence = { sampleRate: 4, channels: [Float32Array.of(0, 0)] };
  assert.equal(await normalizeAudio(silence, { mode: 'peak', peak: '-1' }, new AbortController().signal), silence);
  await assert.rejects(() => normalizeAudio(silence, { mode: 'loudness' }, new AbortController().signal), /BS\.1770/);
});
