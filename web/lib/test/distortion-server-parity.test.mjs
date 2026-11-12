import test from 'node:test';
import assert from 'node:assert/strict';
import { DistortionCore } from '../distortion-core.mjs';

// A port of Effects.Distortion (src/Ample.Audio/Engine/Effects.cs) in the integer arithmetic of the server: the reference the browser core must match.
const RATE = 48000, gainQ15 = (db) => Math.round(32768 * 10 ** (Math.max(-60, Math.min(12, db)) / 20));
function cutoffCoefficient(hz, rate) {
  const x = BigInt(Math.trunc(105414357 * Math.max(1, Math.min(hz, Math.trunc(rate / 2) - 1)) / rate));
  let sum = 1n << 24n, term = 1n << 24n;
  for (let k = 1n; k < 40n && term > 0n; k++) { term = (term * x / k) >> 24n; sum += term; }
  return 32768 - Number((32768n << 24n) / sum);
}
function serverDistortion(x, { drive = 12, hard = false, level = 0, tone = 8000, mix = 100 }) {
  let gain = 32768n;
  for (let left = drive; left > 0; left -= 12) gain = (gain * BigInt(gainQ15(Math.min(left, 12)))) >> 15n;
  const driven = new Array(x.length);
  for (let i = 0; i < x.length; i++) {
    const previous = BigInt(i > 0 ? x[i - 1] : 0); let sum = 0n;
    for (let k = 1n; k <= 4n; k++) {
      const v = ((previous + (((BigInt(x[i]) - previous) * k) >> 2n)) * gain) >> 15n;
      sum += hard ? (v > 32767n ? 32767n : v < -32767n ? -32767n : v) : (v * 32767n) / (32768n + (v < 0n ? -v : v));
    }
    driven[i] = Number(sum >> 2n);
  }
  const a = BigInt(cutoffCoefficient(Math.max(500, Math.min(16000, tone)), RATE)); let state = 0n;
  const lv = BigInt(gainQ15(Math.max(-24, Math.min(12, level))));
  return x.map((d, i) => { state += (a * (BigInt(driven[i]) - state)) >> 15n; const wet = (state * lv) >> 15n; return Number((BigInt(d) * BigInt(100 - mix) + wet * BigInt(mix)) / 100n); });
}
const sine = (amp, hz, n) => Array.from({ length: n }, (_, i) => Math.round(32767 * amp * Math.sin(2 * Math.PI * hz * i / RATE)));
const rms = (a) => Math.sqrt(a.reduce((s, v) => s + v * v, 0) / a.length);

test('the browser core matches the integer server distortion at many mixes, drives, levels and input levels', () => {
  const worst = [];
  for (const mix of [0, 25, 50, 75, 100]) for (const drive of [0, 6, 18, 36]) for (const amp of [0.05, 0.3, 0.9]) for (const hard of [false, true]) {
    const params = { drive, hard, level: -3, tone: 6000, mix }, x = sine(amp, 440, 9600);
    const reference = serverDistortion(x, params);
    const core = new DistortionCore(RATE, 1), out = new Float32Array(x.length);
    core.configure({ drive: String(drive), mode: hard ? 'hard' : 'soft', level: '-3 dB', tone: '6000 Hz', mix: `${mix}%` });
    core.process([Float32Array.from(x, (v) => v / 32768)], [out], 0, x.length);
    const got = Array.from(out, (v) => v * 32768);
    const error = got.map((v, i) => v - reference[i]);
    const db = 20 * Math.log10(Math.max(1e-9, rms(error)) / Math.max(1, rms(reference)));
    worst.push([db, `mix ${mix} drive ${drive} amp ${amp} ${hard ? 'hard' : 'soft'}`]);
  }
  worst.sort((a, b) => b[0] - a[0]);
  assert.ok(worst[0][0] < -40, `error relative to the signal must stay under -40 dB; worst ${worst[0][0].toFixed(1)} dB at ${worst[0][1]}`);
});

test('the blend levels agree with the server within 0.05 dB for a long sine', () => {
  for (const mix of [25, 50, 75]) {
    const x = sine(0.3, 440, 24000), reference = serverDistortion(x, { drive: 12, mix, tone: 16000 });
    const core = new DistortionCore(RATE, 1), out = new Float32Array(x.length);
    core.configure({ drive: '12', mix: String(mix), tone: '16000' });
    core.process([Float32Array.from(x, (v) => v / 32768)], [out], 0, x.length);
    const got = Array.from(out, (v) => v * 32768);
    assert.ok(Math.abs(20 * Math.log10(rms(got.slice(4800)) / rms(reference.slice(4800)))) < 0.05, `mix ${mix}`);
  }
});
