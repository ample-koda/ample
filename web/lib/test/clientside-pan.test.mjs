import test from 'node:test';
import assert from 'node:assert/strict';
import { panAudio } from '../clientside-pan.mjs';

test('pan creates equal-power stereo from mono and balances stereo toward either side', async () => {
  const signal = new AbortController().signal;
  const mono = await panAudio({ sampleRate: 1, channels: [Float32Array.of(1)] }, { pan: '0' }, signal);
  assert.equal(mono.channels.length, 2);
  assert.ok(Math.abs(mono.channels[0][0] - Math.SQRT1_2) < 1e-6);
  const stereo = await panAudio({ sampleRate: 1, channels: [Float32Array.of(1), Float32Array.of(1)] }, { pan: '32' }, signal);
  assert.ok(Math.abs(stereo.channels[0][0]) < 1e-6);
  assert.equal(stereo.channels[1][0], 1);
});
