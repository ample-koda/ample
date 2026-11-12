import assert from 'node:assert/strict';
import test from 'node:test';
import { gateAudio } from './reference/gate-before.mjs';
import { limiterAudio } from './reference/limiter-before.mjs';
import { deesserAudio } from './reference/deesser-before.mjs';
import { compressorAudio as compressorBefore } from './reference/compressor-before.mjs';
import { compressorAudio as compressorNow } from '../clientside-compressor.mjs';
import { gateAudio as gateNow } from '../clientside-gate.mjs';
import { limiterAudio as limiterNow } from '../clientside-limiter.mjs';
import { deesserAudio as deesserNow } from '../clientside-deesser.mjs';
import { dynamicsCore } from '../dynamics-core.mjs';

const burst = (frames, seed) => { const out = new Float32Array(frames); let x = seed; for (let i = 0; i < frames; i++) { x = (x * 1664525 + 1013904223) >>> 0; out[i] = ((x / 2 ** 32) * 2 - 1) * (Math.floor(i / 7000) % 2 ? 1.3 : .02) * Math.sin(i / 3 + seed); } return out; };
const audio = (rate) => ({ sampleRate: rate, channels: [burst(40000, 3), burst(40000, 4)] });
const same = (a, b) => assert.deepEqual(a.channels.map((c) => [...c]), b.channels.map((c) => [...c]));

const cases = {
  gate: [gateAudio, gateNow, [{ threshold: '-30 dB', attack: '5 ms', hold: '40 ms', release: '100 ms' }, { threshold: '-60', attack: '1', hold: '0', release: '5' }]],
  limiter: [limiterAudio, limiterNow, [{ ceiling: '-6 dB', lookahead: '5 ms', release: '100 ms' }, { ceiling: '-1', lookahead: '20', release: '10' }]],
  deesser: [deesserAudio, deesserNow, [{ frequency: '6000 Hz', threshold: '-30 dB', ratio: '4' }, { frequency: '3000', threshold: '-50', ratio: '12' }]],
};

for (const [name, [before, now, sets]] of Object.entries(cases)) {
  test(`${name}: the offline render is bit-identical to the version before the shared core`, async () => {
    for (const params of sets) for (const rate of [44100, 48000]) same(await before(audio(rate), params), await now(audio(rate), params));
  });

  test(`${name}: the live stream in blocks of 128 samples gives the render's samples`, async () => {
    const params = sets[0], rate = 48000, input = audio(rate), reference = await now(input, params), core = dynamicsCore(name, rate, 2);
    core.configure(params);
    const latency = core.latency || 0, total = input.channels[0].length + latency;
    const padded = input.channels.map((c) => { const copy = new Float32Array(total); copy.set(c); return copy; }), out = input.channels.map(() => new Float32Array(total));
    for (let start = 0; start < total; start += 128) core.process(padded, out, start, Math.min(total, start + 128));
    same({ channels: out.map((c) => c.slice(latency)) }, reference);
  });
}

test('the limiter reports its look-ahead as latency and never lets a peak through', async () => {
  const core = dynamicsCore('limiter', 48000, 2); core.configure({ ceiling: '-6 dB', lookahead: '5 ms' });
  assert.equal(core.latency, 240);
  const out = await limiterNow(audio(48000), { ceiling: '-6 dB', lookahead: '5 ms', release: '100 ms' });
  const peak = Math.max(...out.channels.flatMap((c) => [...c].map(Math.abs)));
  assert.ok(peak <= 0.5012 + 1e-3, `peak ${peak}`);
});

test('compressor: the offline render is bit-identical to the version before the shared core, with and without a key', async () => {
  const params = [{ threshold: '-30 dB', ratio: '6', attack: '5 ms', release: '80 ms', makeup: '3 dB' }, { threshold: '-20', ratio: '3', attack: '1', release: '5', keyFilter: '800 Hz' }, { threshold: '-40 dB', keyFilter: '200' }];
  for (const p of params) for (const rate of [44100, 48000]) {
    same(await compressorBefore(audio(rate), p, null), await compressorNow(audio(rate), p, null));
    const key = { sampleRate: rate, channels: [burst(30000, 9), burst(30000, 10)] };   // a key shorter than the program
    same(await compressorBefore(audio(rate), p, key), await compressorNow(audio(rate), p, key));
  }
  const other = { sampleRate: 22050, channels: [burst(30000, 11)] };
  same(await compressorBefore(audio(48000), params[1], other), await compressorNow(audio(48000), params[1], other));
});

test('compressor: the live stream in blocks of 128 samples gives the samples of the render, with a key', async () => {
  const rate = 48000, input = audio(rate), key = { sampleRate: rate, channels: [burst(40000, 21), burst(40000, 22)] }, params = { threshold: '-30 dB', ratio: '6', keyFilter: '600 Hz' };
  const reference = await compressorNow(input, params, key), core = dynamicsCore('compressor', rate, 2);
  core.configure(params);
  const out = input.channels.map(() => new Float32Array(40000));
  for (let start = 0; start < 40000; start += 128) core.process(input.channels, out, start, Math.min(40000, start + 128), key.channels);
  same({ channels: out }, reference);
});
