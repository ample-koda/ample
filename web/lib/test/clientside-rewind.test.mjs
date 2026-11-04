import test from 'node:test';
import assert from 'node:assert/strict';
import { parseRewindEvents, rewindAudio } from '../clientside-rewind.mjs';

const RATE = 16000;
// twelve seconds in which every sample has its own value (as in the C# test), in 16-bit steps scaled to -1..1
const source = () => ({ sampleRate: RATE, channels: [Float32Array.from({ length: 12 * RATE }, (_, i) => Math.trunc(i / 8) / 32768)] });
const same = (a, b) => assert.ok(Math.abs(a - b) < 1e-6, `${a} is not ${b}`);

test('the music stops, a scratch is heard and the music goes on five seconds back, like the C# renderer', () => {
  const input = source();
  const { audio } = rewindAudio(input, { events: 'at 6s scratch 2s back 5s', fade: '0' }, RATE, null);
  const out = audio.channels[0], src = input.channels[0];
  assert.equal(out.length, src.length);
  for (let i = 0; i < 6 * RATE; i += 997) same(out[i], src[i]);
  assert.ok([...Array(RATE).keys()].some((k) => Math.abs(out[7 * RATE + k] - src[7 * RATE + k]) > 1e-4));
  assert.ok(Math.abs(out[8 * RATE - 1]) < 2000 / 32768);
  for (let k = 0; k < 4 * RATE; k += 997) same(out[8 * RATE + k], src[RATE + k]);
});

test('a jump back stops at the start, a negative back skips forward, the end stops or loops and the region holds only the scratches', () => {
  const input = source(), src = input.channels[0];
  const clamped = rewindAudio(input, { events: 'at 1s scratch 1s back 10s', fade: '0' }, RATE, null).audio.channels[0];
  for (let k = 0; k < RATE; k += 499) same(clamped[2 * RATE + k], src[k]);
  const skipped = rewindAudio(input, { events: 'at 2s scratch 1s back -3s', fade: '0' }, RATE, null).audio.channels[0];
  for (let k = 0; k < RATE; k += 499) same(skipped[3 * RATE + k], src[5 * RATE + k]);
  const params = { events: 'at 10s scratch 1s back -5s', fade: '0' };
  assert.ok(rewindAudio(input, params, RATE, null).audio.channels[0].slice(11 * RATE).every((v) => v === 0));
  const looped = rewindAudio(input, { ...params, end: 'loop' }, RATE, null).audio.channels[0];
  for (let k = 0; k < RATE; k += 499) same(looped[11 * RATE + k], src[3 * RATE + k]);
  const { region } = rewindAudio(input, { events: 'at 2s scratch 1s back 1s; at 7s scratch 1s back 2s', fade: '0' }, RATE, null);
  const r = region.channels[0];
  assert.ok(r.slice(0, 2 * RATE).every((v) => v === 0) && r.slice(3 * RATE, 7 * RATE).every((v) => v === 0) && r.slice(8 * RATE).every((v) => v === 0));
  assert.ok(r.slice(2 * RATE, 3 * RATE).some((v) => v !== 0) && r.slice(7 * RATE, 8 * RATE).some((v) => v !== 0));
});

test('a real sample on fx is the scratch, the record start-up runs behind the straight cut and unreadable events say why', () => {
  const input = source();
  const fx = { sampleRate: RATE, channels: [new Float32Array(3 * RATE).fill(5000 / 32768)] };
  const out = rewindAudio(input, { events: 'at 4s scratch 2s back 1s', fade: '0' }, RATE, fx).audio.channels[0];
  for (let k = 0; k < 2 * RATE - 1; k += 499) same(out[4 * RATE + k], 5000 / 32768);
  assert.throws(() => rewindAudio(input, { events: 'at 4s scratch 2s back 1s', sound: 'fx' }, RATE, null), /needs a sample/);
  const straight = rewindAudio(input, { events: 'at 6s scratch 2s back 5s', fade: '0' }, RATE, null).audio.channels[0];
  const smeared = rewindAudio(input, { events: 'at 6s scratch 2s back 5s', fade: '0', smear: '1000' }, RATE, null).audio.channels[0];
  assert.ok(smeared[8 * RATE + 8000] < straight[8 * RATE + 8000] - 250 / 32768);
  const lag = (straight[10 * RATE] - smeared[10 * RATE]) * 32768 * 8;
  assert.ok(lag > 0.1 * RATE && lag < 0.3 * RATE, `lag ${lag}`);
  assert.throws(() => parseRewindEvents('at 6 seconds scratch two', RATE, RATE), /not a time|not known/);
  assert.equal(parseRewindEvents('at 4.5', RATE, RATE)[0].at, 4.5 * RATE);
  assert.deepEqual(parseRewindEvents('at 1s scratch 500ms back -1s', RATE, RATE), [{ at: RATE, scratch: RATE / 2, back: -RATE }]);
});
