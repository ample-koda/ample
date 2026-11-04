import test from 'node:test';
import assert from 'node:assert/strict';
import { autoSlices, chopAudio, chopSettings, parsePattern, parseStarts, sliceStarts } from '../clientside-chop.mjs';

const RATE = 22050;
// three "words" of different level: 0.1-0.4 s, 0.7-1.0 s, 1.3-1.6 s
const words = () => {
  const x = new Float32Array(2 * RATE);
  for (const [from, to, level] of [[0.1, 0.4, 0.1], [0.7, 1.0, 0.25], [1.3, 1.6, 0.4]]) for (let i = Math.floor(from * RATE); i < Math.floor(to * RATE); i++) x[i] = level * Math.sin(2 * Math.PI * 300 * i / RATE);
  return x;
};
const rms = (a, from, to) => { let s = 0; const lo = Math.floor(from * RATE), hi = Math.floor(to * RATE); for (let i = lo; i < hi; i++) s += a[i] * a[i]; return Math.sqrt(s / (hi - lo)); };

test('the words are found, and a long gap setting closes the silences', () => {
  const starts = autoSlices(words(), RATE, 50, 150);
  assert.equal(starts.length, 3);
  assert.ok(starts[0] / RATE > 0.05 && starts[0] / RATE < 0.1);
  assert.equal(autoSlices(words(), RATE, 50, 700).length, 1);
  assert.equal(sliceStarts(words(), RATE, { slices: '0.2 0.9' }).length, 2);
  assert.throws(() => parseStarts('0.5 0.2', RATE, 2 * RATE));
});

test('the pattern reads steps, rests and repeats and refuses a slice that is not there', () => {
  assert.deepEqual(parsePattern('1 3 - 2*3', 3), [{ slice: 0, times: 1 }, { slice: 2, times: 1 }, { slice: -1, times: 1 }, { slice: 1, times: 3 }]);
  assert.equal(parsePattern('', 3).length, 3);
  assert.throws(() => parsePattern('1 4', 3));
  assert.throws(() => parsePattern('1*0', 3));
  assert.equal(chopSettings({ gate: '50 %', repeat: '2' }).gate, 0.5);
});

test('the slices land on the beat in the order of the pattern', async () => {
  // 120 bpm: a beat is half a second; slice 3 (loud), a rest, slice 1 (quiet)
  const out = await chopAudio({ sampleRate: RATE, channels: [words()] }, { pattern: '3 - 1' }, 120);
  assert.equal(out.channels[0].length, RATE * 1.5);
  assert.ok(rms(out.channels[0], 0.05, 0.3) > 0.2);
  assert.ok(rms(out.channels[0], 0.55, 0.95) < 0.001);
  assert.ok(rms(out.channels[0], 1.05, 1.3) > 0.04 && rms(out.channels[0], 1.05, 1.3) < 0.12);
  const stutter = await chopAudio({ sampleRate: RATE, channels: [words()] }, { pattern: '2*2', gate: '50 %' }, 120);
  assert.ok(rms(stutter.channels[0], 0.01, 0.11) > 0.1 && rms(stutter.channels[0], 0.14, 0.23) < 0.001 && rms(stutter.channels[0], 0.26, 0.36) > 0.1);
  assert.equal((await chopAudio({ sampleRate: RATE, channels: [words()] }, { pattern: '1', repeat: '2' }, 120)).channels[0].length, RATE);
});
