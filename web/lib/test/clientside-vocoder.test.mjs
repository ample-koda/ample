import test from 'node:test';
import assert from 'node:assert/strict';
import { vocodeAudio, vocoderSettings } from '../clientside-vocoder.mjs';

const RATE = 16000, N = RATE;
const saw = (hz) => Float32Array.from({ length: N }, (_, i) => 0.3 * (2 * ((i * hz / RATE) % 1) - 1));
const rms = (a) => Math.sqrt(a.reduce((s, v) => s + v * v, 0) / a.length);

test('the settings are read from the texts of the block and clamped', () => {
  const s = vocoderSettings({ bands: '24', release: '60 ms', width: '140 %', sibilance: '45 %', gain: '5 dB', shift: '-30 st' });
  assert.equal(s.bands, 24); assert.equal(s.release, 60); assert.equal(s.width, 1.4); assert.equal(s.sibilance, 0.45); assert.equal(s.gain, 5); assert.equal(s.shift, -24);
});

test('a silent voice makes a silent result, a voice makes the carrier speak at the level of the carrier', async () => {
  const carrier = { sampleRate: RATE, channels: [saw(110)] };
  const silent = await vocodeAudio({ sampleRate: RATE, channels: [new Float32Array(N)] }, carrier, {});
  assert.equal(rms(silent.channels[0]), 0);
  // a voice that is on for the first half of the second only
  const voice = Float32Array.from({ length: N }, (_, i) => (i < N / 2 ? Math.sin(2 * Math.PI * 300 * i / RATE) * 0.4 : 0));
  const out = await vocodeAudio({ sampleRate: RATE, channels: [voice] }, carrier, { sibilance: '0 %' });
  assert.equal(out.channels[0].length, N);
  assert.ok(rms(out.channels[0].subarray(0, N / 2 - 800)) > 5 * rms(out.channels[0].subarray(N - 1600)));
  assert.ok(Number.isFinite(rms(out.channels[0])));
});

test('mix 0 gives the carrier back and enabled off passes it through', async () => {
  const carrier = { sampleRate: RATE, channels: [saw(110)] }, voice = { sampleRate: RATE, channels: [saw(200)] };
  const dry = await vocodeAudio(voice, carrier, { mix: '0 %' });
  assert.ok(dry.channels[0].every((v, i) => Math.abs(v - carrier.channels[0][i]) < 1e-6));
  assert.equal(await vocodeAudio(voice, carrier, { enabled: 'off' }), carrier);
});
