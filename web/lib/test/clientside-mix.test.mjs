import test from 'node:test';
import assert from 'node:assert/strict';
import { masterAudioBuffer, mixAudioBuffers, sendAudioBuffers } from '../clientside-mix.mjs';
import { limiterAudio } from '../clientside-limiter.mjs';

const audio = (left, right, sampleRate) => ({ sampleRate, channels: [Float32Array.from(left), ...(right ? [Float32Array.from(right)] : [])] });
const close = (actual, expected, tolerance) => assert.ok(Math.abs(actual - expected) <= tolerance, `${actual} is not within ${tolerance} of ${expected}`);

test('local mixer orders lanes, applies dB gain and keeps a complete stereo sum', async () => {
  const mixed = await mixAudioBuffers([{ audio: audio([0.25], [0.5], 48000), index: 0 }, { audio: audio([0.5], null, 48000), index: 1 }], { gains: '0 -6', pan: 'c c', channels: 'auto' });
  assert.equal(mixed.channels.length, 2);
  close(mixed.channels[0][0], 0.25 + 0.5 * 10 ** (-6 / 20), 0.002);
  close(mixed.channels[1][0], 0.5 + 0.5 * 10 ** (-6 / 20), 0.002);
});

test('mixer accepts all sixteen lanes and applies left/right lane placement', async () => {
  const inputs = Array.from({ length: 16 }, (_, index) => ({ audio: audio([index === 0 ? 0.2 : 0], null, 48000), index }));
  const mixed = await mixAudioBuffers(inputs, { gains: '0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0', pan: 'l r c c c c c c c c c c c c c c', channels: 'stereo' });
  close(mixed.channels[0][0], 0.2, 0.002);
  assert.equal(mixed.channels[1][0], 0);
});

test('bus applies its final gain and mute without dropping the input', async () => {
  const source = [{ audio: audio([0.5], null, 48000), index: 0 }];
  close((await mixAudioBuffers(source, { gains: '0', busGain: '-6', mute: false })).channels[0][0], 0.5 * 10 ** (-6 / 20), 0.002);
  assert.equal((await mixAudioBuffers(source, { gains: '0', busGain: '0', mute: true })).channels[0][0], 0);
});

test('send returns separate dry through and attenuated send outputs', async () => {
  const result = await sendAudioBuffers([{ audio: audio([0.5], null, 48000), index: 0 }, { audio: audio([0.25], null, 48000), index: 1 }], { levels: '-6 -12', channels: 'mono' });
  close(result.through.channels[0][0], 0.75, 0.002);
  close(result.send.channels[0][0], 0.5 * 10 ** (-6 / 20) + 0.25 * 10 ** (-12 / 20), 0.002);
});

test('master applies a ceiling and selects its requested bars', async () => {
  const source = audio([0, 0.9, -0.9, 0.2, 0.3, 0.4, 0.5, 0.6], null, 1);
  const result = await masterAudioBuffer(source, { limiter: '-6 dB', slice_from: '2', slice_to: '2' }, 60);
  assert.equal(result.channels[0].length, 4);
  assert.ok(result.channels[0].every((sample) => Math.abs(sample) <= 10 ** (-6 / 20)));
});

test('local mix refuses incompatible sample rates instead of silently resampling', async () => {
  await assert.rejects(mixAudioBuffers([{ audio: audio([0], null, 44100), index: 0 }, { audio: audio([0], null, 48000), index: 1 }], {}), /different sample rates/);
});

test('local mix observes cancellation between audio chunks', async () => {
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(mixAudioBuffers([{ audio: audio(new Array(70000).fill(0.2), null, 48000), index: 0 }], {}, controller.signal), { name: 'AbortError' });
});

test('master limits the full mix before slicing and keeps target advisory', async () => {
  const source = audio(Float32Array.from({ length: 16000 }, (_, i) => Math.sin(i * 0.2) * 0.9), null, 8000);
  const expected = await limiterAudio(source, { ceiling: -6, release: 100, lookahead: 5 });
  const options = { limiter: '-6 dB', slice_from: '2', slice_to: '2', target: '-14 LUFS' };
  const result = await masterAudioBuffer(source, options, 240);
  assert.deepEqual(result.channels[0], expected.channels[0].slice(8000, 16000));
  assert.deepEqual((await masterAudioBuffer(source, { ...options, target: '-23 LUFS' }, 240)).channels, result.channels);
  assert.equal(await masterAudioBuffer(source, { enabled: 'off' }, 240), source);
  const controller = new AbortController(); controller.abort();
  await assert.rejects(masterAudioBuffer(source, options, 240, controller.signal), { name: 'AbortError' });
});
