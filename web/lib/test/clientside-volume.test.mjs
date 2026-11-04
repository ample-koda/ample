import test from 'node:test';
import assert from 'node:assert/strict';
import { volumeAudio } from '../clientside-volume.mjs';

test('volume applies common/channel gain, mono fold and output mutes', () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.5), Float32Array.of(-0.5)] };
  const result = volumeAudio(audio, { gain: '6.0206 dB', left_gain: '0 dB', right_gain: '-12.0412 dB', mono: 'off', left: 'on', right: 'on' });
  assert.ok(Math.abs(result.channels[0][0] - 1) < 1e-5);
  assert.ok(Math.abs(result.channels[1][0] + 0.25) < 1e-5);
  const mono = volumeAudio(audio, { mono: 'on', left: 'on', right: 'off' });
  assert.equal(mono.channels[0][0], 0);
  assert.equal(mono.channels[1][0], 0);
  assert.equal(volumeAudio(audio, { enabled: 'off' }), audio);
});
