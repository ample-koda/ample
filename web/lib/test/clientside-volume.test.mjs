import test from 'node:test';
import assert from 'node:assert/strict';
import { volumeAudio } from '../clientside-volume.mjs';
import { volumeCurve, gainQ15Wide, automationFocus } from '../volume-auto.mjs';

test('volume applies common/channel gain, mono fold and output mutes (whole dB, as the server)', () => {
  const audio = { sampleRate: 1, channels: [Float32Array.of(0.5), Float32Array.of(-0.5)] };
  const result = volumeAudio(audio, { gain: '6.0206 dB', left_gain: '0 dB', right_gain: '-12.0412 dB', mono: 'off', left: 'on', right: 'on' });
  assert.ok(Math.abs(result.channels[0][0] - 0.5 * 65381 / 32768) < 1e-6, 'the 6.0206 dB of the text is the 6 dB of the server');
  assert.ok(Math.abs(result.channels[1][0] + 0.5 * 65381 / 32768 * 8231 / 32768) < 1e-6);
  const mono = volumeAudio(audio, { mono: 'on', left: 'on', right: 'off' });
  assert.equal(mono.channels[0][0], 0);
  assert.equal(mono.channels[1][0], 0);
  assert.equal(volumeAudio(audio, { enabled: 'off' }), audio);
});

test('an automated gain is the server curve: points joined by straight lines in the gain, not in dB', () => {
  const rate = 1000, bar = 2000, curve = volumeCurve('auto(1:0, 2:-18)', bar, rate, 1);
  assert.equal(curve(0), 1);
  assert.equal(curve(bar), gainQ15Wide(-18) / 32768);
  assert.ok(Math.abs(curve(bar / 2) - (1 + gainQ15Wide(-18) / 32768) / 2) < 1e-4, 'halfway is about -5 dB, not -9');
  assert.equal(volumeCurve('auto(2:-6)', bar, rate, 1)(10), gainQ15Wide(-6) / 32768, 'before the first point it is the first value');
  assert.equal(volumeCurve('auto(1:0, 2:-18)', bar, rate, 2)(0), gainQ15Wide(-18) / 32768, 'a take that starts at bar 2 is counted from the song');
  assert.equal(volumeCurve('-6 dB', bar, rate, 1), null);
});

test('fades: the Q15 amount of the server, equal power with an integer square root', () => {
  const rate = 1000, bar = 2000, fade = volumeCurve('fadein(1s, 2s, equal)', bar, rate, 1);
  assert.equal(fade(1000), 0); assert.equal(fade(2000), Math.floor(Math.sqrt(16384 * 32768)) / 32768); assert.equal(fade(3000), 1); assert.equal(fade(5000), 1);
  const out = volumeCurve('fadeout(2s, 1s, exp)', bar, rate, 1);
  assert.equal(out(2000), 1); assert.equal(out(3000), 0); assert.equal(out(2500), Math.floor(16384 * 16384 / 32768) / 32768);
  assert.equal(volumeCurve('fadein(0.5, 1)', bar, rate, 1), null, 'a bar position below 1 is not read by the server either');
  assert.equal(automationFocus('fadein(5.65s, 0.064s, lin)', bar, 44100), Math.floor(5.65 * 44100));
});

test('the automated volume is applied per sample', () => {
  const audio = { sampleRate: 1000, channels: [new Float32Array(3000).fill(0.5), new Float32Array(3000).fill(0.5)] };
  const result = volumeAudio(audio, { gain: 'fadein(1s, 1s, lin)', left: 'on', right: 'on' }, 120, 1);
  assert.equal(result.channels[0][500], 0); assert.ok(Math.abs(result.channels[0][1500] - 0.25) < 1e-4); assert.equal(result.channels[1][2500], 0.5);
});
