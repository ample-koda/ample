import test from 'node:test';
import assert from 'node:assert/strict';
import { delayAudio } from '../clientside-delay.mjs';

// Bug 28 (Studio bug hunt 2026-11-24): the delay, phaser and eq previews read "187.5 ms" or "37.5%" with decimals, while the server reads the leading whole number
// (Effects.cs Read), so the preview echoed 22 samples apart from the build and the gap grew with every repeat.
test('a delay time with a decimal part echoes where the server echoes it', async () => {
  const rate = 44100, frames = 20000, click = new Float32Array(frames); click[0] = 1;
  const signal = new AbortController().signal;
  const echoed = await delayAudio({ sampleRate: rate, channels: [click] }, { time: '187.5 ms', feedback: '0%', mix: '100%' }, signal);
  const first = echoed.channels[0].findIndex((v, i) => i > 0 && Math.abs(v) > 1e-6);
  assert.equal(first, Math.floor(187 * rate / 1000));
});
