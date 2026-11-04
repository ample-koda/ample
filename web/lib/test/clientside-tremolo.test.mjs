import test from 'node:test';
import assert from 'node:assert/strict';
import { tremoloAudio } from '../clientside-tremolo.mjs';

test('tremolo supports synchronized sine gain and keeps the expected mono output', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(1, 1, 1, 1)] };
  const result = await tremoloAudio(audio, { mode: 'tremolo', sync: '1/4', depth: '100', phase: '0', shape: 'sine' }, 60, 1, new AbortController().signal);
  assert.equal(result.channels.length, 1);
  assert.deepEqual([...result.channels[0]].map((value) => Math.round(value * 1e6) / 1e6), [0.5, 1, 0.5, 0]);
});

test('autopan turns mono into stereo and follows a square wave without mutating the input', async () => {
  const audio = { sampleRate: 4, channels: [Float32Array.of(1, 1, 1, 1)] };
  const result = await tremoloAudio(audio, { mode: 'autopan', rate: '1', depth: '100', phase: '0', shape: 'square', sync: 'off' }, 60, 1, new AbortController().signal);
  assert.equal(result.channels.length, 2);
  assert.deepEqual([...result.channels[0]], [1, 1, 0, 0]);
  assert.deepEqual([...result.channels[1]], [0, 0, 1, 1]);
  assert.deepEqual([...audio.channels[0]], [1, 1, 1, 1]);
});

test('the trance gate follows the step pattern in time with the tempo and the default shapes are unchanged', async () => {
  // 60 bpm, rate 1600: a bar is 6400 samples, sync 1/16 gives steps of 400 samples; the fade is 3 samples
  const rate = 1600, flat = { sampleRate: rate, channels: [new Float32Array(6400).fill(1)] };
  const result = await tremoloAudio(flat, { mode: 'tremolo', sync: '1/16', depth: '100', shape: 'gate', pattern: '"x.xx"' }, 60, 1, new AbortController().signal);
  const out = result.channels[0];
  assert.equal(out[200], 1);      // step 0 open
  assert.equal(out[600], 0);      // step 1 closed
  assert.equal(out[1000], 1);     // step 2 open
  assert.equal(out[1400], 1);     // step 3 open
  assert.equal(out[1800], 1);     // the pattern repeats: step 4 is open
  assert.equal(out[2200], 0);     // step 5 closed
  assert.ok(out[401] > 0 && out[401] < 1);   // a fade, not a click
  const depth = await tremoloAudio(flat, { mode: 'tremolo', sync: '1/16', depth: '50', shape: 'gate', pattern: 'x.' }, 60, 1, new AbortController().signal);
  assert.equal(depth.channels[0][600], 0.5);
});

test('gate levels: a to i are 10 to 90 percent open, x and 1 stay full, anything else is closed', async () => {
  const rate = 1600, flat = { sampleRate: rate, channels: [new Float32Array(6400).fill(1)] };
  const result = await tremoloAudio(flat, { mode: 'tremolo', sync: '1/16', depth: '100', shape: 'gate', pattern: 'xe1.' }, 60, 1, new AbortController().signal);
  const out = result.channels[0];
  assert.equal(out[200], 1);
  assert.ok(Math.abs(out[600] - 0.5) < 1e-6);
  assert.equal(out[1000], 1);
  assert.equal(out[1400], 0);
});
