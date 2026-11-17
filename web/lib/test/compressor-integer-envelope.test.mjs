import test from 'node:test';
import assert from 'node:assert/strict';
import { CompressorCore } from '../dynamics-core.mjs';

// Effects.Compress of the server in whole numbers (16-bit steps, integer division that truncates toward zero), linked channels, no key.
function serverCompress(channels, rate, { threshold, ratio, attack, release, makeup }) {
  const limit = Math.trunc(Math.trunc(10 ** (threshold / 20) * 32768) * 32767 / 32768), atk = Math.max(1, Math.trunc(attack * rate / 1000)), rel = Math.max(1, Math.trunc(release * rate / 1000)), gainUp = 10 ** (makeup / 20);
  const out = channels.map((c) => new Float32Array(c.length)); let env = 0;
  for (let i = 0; i < channels[0].length; i++) {
    let level = 0; for (const c of channels) level = Math.max(level, Math.abs(Math.round(c[i] * 32768)));
    env += Math.trunc((level - env) / (level > env ? atk : rel));
    const gain = env > limit && env > 0 ? Math.trunc((limit + Math.trunc((env - limit) / ratio)) * 32768 / env) / 32768 : 1;
    channels.forEach((c, k) => { out[k][i] = c[i] * gain * gainUp; });
  }
  return out;
}
const run = (channels, params, rate = 48000) => { const core = new CompressorCore(rate); core.configure(params); const out = channels.map((c) => new Float32Array(c.length)); core.process(channels, out, 0, channels[0].length); return out; };
const noise = (n, seed, amp) => { let s = seed; return Float32Array.from({ length: n }, (_, i) => { s = (s * 1103515245 + 12345) & 0x7fffffff; return Math.round(((s / 0x7fffffff) * 2 - 1) * amp * Math.sin(i / 3000) * 32768) / 32768; }); };
const params = { threshold: '-24 dB', ratio: '2', attack: '20 ms', release: '180 ms', makeup: '2 dB' };

test('the live compressor follows the server in whole numbers: a vocal-like signal stays within -55 dB of the integer reference', () => {
  const left = noise(48000 * 4, 7, 0.6), right = noise(48000 * 4, 11, 0.5);
  const mine = run([left, right], params), reference = serverCompress([left, right], 48000, { threshold: -24, ratio: 2, attack: 20, release: 180, makeup: 2 });
  let difference = 0, energy = 0;
  for (let c = 0; c < 2; c++) for (let i = 0; i < left.length; i++) { const d = mine[c][i] - reference[c][i]; difference += d * d; energy += reference[c][i] ** 2; }
  assert.ok(10 * Math.log10(difference / energy) < -55, `distance ${(10 * Math.log10(difference / energy)).toFixed(1)} dB`);
});

test('after a loud burst the envelope stops above the threshold (a step below the release time is lost), so a quiet tone stays compressed', () => {
  const rate = 48000, n = rate * 2, input = new Float32Array(n);
  for (let i = 0; i < n; i++) input[i] = (i < rate * 0.3 ? 0.5 : 0.04) * Math.sin(2 * Math.PI * 440 * i / rate);
  const out = run([input], { threshold: '-24 dB', ratio: '4', attack: '10 ms', release: '180 ms', makeup: '0 dB' })[0];
  let a = 0, b = 0;
  for (let i = rate * 1.5; i < n; i++) { a += out[i] ** 2; b += input[i] ** 2; }
  assert.ok(10 * Math.log10(a / b) < -0.5, 'float math released the gain back to 0 dB here (about +0 dB); the server holds it');
  const core = new CompressorCore(rate); core.configure({ release: '180 ms' });
  assert.equal(Number.isInteger(core.envelope), true);
});
