import test from 'node:test';
import assert from 'node:assert/strict';
import { clickBars, clickAudio } from '../clientside-click.mjs';

test('click: one burst per beat, the first beat of a bar louder, integer values like the server', () => {
  const rate = 44100, bar = 88200, s = clickBars({}, 2, bar, rate);
  assert.equal(s.length, 2 * bar);
  // accent: peak 16000, period 29 (44100 / 1500), length 1102 (25 ms); the first sample is the full peak
  assert.equal(s[0] * 32768, 16000);
  assert.equal(s[bar / 4] * 32768, 9000);   // a normal beat: peak 9000
  assert.equal(s[bar / 4 - 1], 0);          // silent before the next click
  // k = 14 is still in the first half period (period / 2 = 14, 14 / 14 = 1 -> negative)
  assert.ok(s[14] < 0);
  assert.equal(clickBars({ accent: 'off' }, 1, bar, rate)[0] * 32768, 9000);
  const fourth = clickBars({ per_beat: '4' }, 1, bar, rate);
  assert.equal(fourth[Math.trunc(bar / 16)] * 32768, 9000);
});

test('click audio is silent outside the placements of the block', () => {
  const bar = 1000, audio = clickAudio({}, [{ at: 2, length: 1 }], bar, 11025, 1, 3);
  const c = audio.channels[0];
  assert.equal(c.length, 3 * bar);
  assert.ok(c.subarray(0, bar).every((v) => v === 0));
  assert.ok(c[bar] !== 0);
  assert.ok(c.subarray(2 * bar).every((v) => v === 0));
});
