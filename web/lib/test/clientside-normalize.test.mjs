import test from 'node:test';
import assert from 'node:assert/strict';
import { normalizeAudio, normalizeGain } from '../clientside-normalize.mjs';

test('peak normalization reaches the target and respects the maximum gain', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(0.25, -0.5)] };
  const target = await normalizeAudio(audio, { mode: 'peak', peak: '-6 dBFS', max_gain: '48' }, new AbortController().signal);
  assert.ok(Math.abs(target.channels[0][1] + 10 ** (-6 / 20)) < 1e-6);
  const limited = await normalizeAudio(audio, { mode: 'peak', peak: '0', max_gain: '3' }, new AbortController().signal);
  assert.ok(Math.abs(limited.channels[0][1] + 0.5 * 10 ** (3 / 20)) < 1e-6);
});

test('silent input stays unchanged in both modes', async () => {
  const silence = { sampleRate: 4, channels: [Float32Array.of(0, 0)] };
  assert.equal(await normalizeAudio(silence, { mode: 'peak', peak: '-1' }, new AbortController().signal), silence);
  assert.equal(await normalizeAudio(silence, { mode: 'loudness' }, new AbortController().signal), silence);
});

const sine = (hz, amplitude, seconds, rate) => Float32Array.from({ length: Math.round(seconds * rate) }, (_, i) => amplitude * Math.sin(2 * Math.PI * hz * i / rate));

test('loudness mode gains the sound to the target loudness, never past -0.1 dBFS, like the server', async () => {
  const rate = 48000, quiet = { sampleRate: rate, channels: [sine(997, 0.05, 3, rate)] };   // -26 dBFS peak, mono
  const { gainDb, lufs, peakDb } = await normalizeGain(quiet, { mode: 'loudness', loudness: '-18' });
  assert.ok(Math.abs(lufs - (-26 - 3.01)) < 0.1, 'lufs ' + lufs);   // a mono sine of amplitude A: 10 log10(A^2 / 2) = -29 LUFS
  assert.ok(Math.abs(gainDb - (-18 - lufs)) < 1e-9 && gainDb > 10);
  const out = await normalizeAudio(quiet, { mode: 'loudness', loudness: '-18' });
  const peak = out.channels[0].reduce((m, v) => Math.max(m, Math.abs(v)), 0);
  assert.ok(Math.abs(20 * Math.log10(peak) - (peakDb + gainDb)) < 1e-3);
  const hot = await normalizeGain(quiet, { mode: 'loudness', loudness: '-3' });   // a target that would clip is held at -0.1 dBFS
  assert.ok(Math.abs(hot.peakDb + hot.gainDb - -0.1) < 1e-9);
  assert.equal((await normalizeGain(quiet, { mode: 'loudness', loudness: '-18', max_gain: '6' })).gainDb, 6);
  assert.equal((await normalizeGain({ sampleRate: rate, channels: [sine(997, .1, .2, rate)] }, { mode: 'loudness', loudness: '-18' })).gainDb, 0);   // shorter than 400 ms
});
