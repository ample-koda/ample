import test from 'node:test';
import assert from 'node:assert/strict';
import { LlamaStream } from '../llama-stream.mjs';
import { detectPitch } from '../clientside-autotune.mjs';

const RATE = 22050;
const run = (stream, seconds, from = 0) => {
  const left = new Float32Array(Math.round(seconds * RATE)), right = new Float32Array(left.length);
  for (let i = 0; i < left.length; i += 128) stream.render(left.subarray(i, i + 128), right.subarray(i, i + 128), Math.min(128, left.length - i), from + i / RATE);   // blocks of 128 like the worklet
  return { left, right };
};
const pitchClass = (x, from, to) => { const v = [...detectPitch(x.subarray(from, to), RATE).midi].filter((m) => !Number.isNaN(m)).sort((a, b) => a - b); return ((v[v.length >> 1] % 12) + 12) % 12; };
const off = (a, b) => Math.abs((((a - b) % 12) + 18) % 12 - 6);

test('a note is sung at its pitch, silence before and after it', () => {
  const stream = new LlamaStream(RATE);
  stream.settings({ vibrato: 0, releaseMs: 60 });
  stream.noteOn(57, 100, 0.5); stream.noteOff(57, 2.0);
  const { left } = run(stream, 3);
  const level = (a, b) => left.subarray(Math.round(a * RATE), Math.round(b * RATE)).reduce((m, v) => Math.max(m, Math.abs(v)), 0);
  assert.ok(level(0, 0.45) < 1e-6, 'silent before the note');
  assert.ok(level(1, 1.8) > 0.05 && level(1, 1.8) < 0.95, 'sings at a healthy level');
  assert.ok(level(2.4, 3) < 0.01, 'silent after the release');
  assert.ok(off(pitchClass(left, Math.round(1 * RATE), Math.round(1.8 * RATE)), 57 % 12) < 0.2);
});

test('a second note while the first is held glides to it (one voice), and letting go of it returns to the first', () => {
  const stream = new LlamaStream(RATE);
  stream.settings({ vibrato: 0, glideMs: 100 });
  stream.noteOn(57, 100, 0); stream.noteOn(64, 100, 1.0); stream.noteOff(64, 2.0);
  const { left } = run(stream, 3);
  assert.ok(off(pitchClass(left, Math.round(1.5 * RATE), Math.round(1.9 * RATE)), 64 % 12) < 0.25);
  assert.ok(off(pitchClass(left, Math.round(2.5 * RATE), Math.round(2.95 * RATE)), 57 % 12) < 0.25);
});

test('vowel and mouth can be turned while it sings, unison is stereo, and the same events give the same samples', () => {
  const make = () => { const s = new LlamaStream(RATE, 5); s.settings({ unison: 3 }); s.noteOn(60, 100, 0); return s; };
  const a = make(), b = make();
  const ra = run(a, 0.5), rb = run(b, 0.5);
  assert.deepEqual([...ra.left.subarray(0, 200)], [...rb.left.subarray(0, 200)]);
  assert.notDeepEqual([...ra.left.subarray(8000, 8100)], [...ra.right.subarray(8000, 8100)]);
  const before = run(a, 0.3, 0.5).left;
  a.settings({ vowel: 0.0, mouth: 1 });
  const after = run(a, 0.3, 0.8).left;
  assert.notDeepEqual([...before.subarray(1000, 1100)], [...after.subarray(1000, 1100)]);
});

test('the pitch wheel bends the voice at once: +12 semitones is an octave up, and back to 0 returns it', () => {
  const median = (x) => { const v = [...detectPitch(x, RATE).midi].filter((m) => !Number.isNaN(m)).sort((a, b) => a - b); return v[Math.floor(v.length / 2)]; };
  const stream = new LlamaStream(RATE);
  stream.settings({ vibrato: 0 });
  stream.noteOn(57, 100, 0);
  run(stream, 0.6);
  const before = median(run(stream, 0.8, 0.6).left);
  stream.bend(12);
  run(stream, 0.3, 1.4);
  const up = median(run(stream, 0.8, 1.7).left);
  stream.bend(0);
  run(stream, 0.3, 2.5);
  const back = median(run(stream, 0.8, 2.8).left);
  assert.ok(Math.abs(before - 57) < 0.3, 'it sings the note: ' + before);
  assert.ok(Math.abs(up - 69) < 0.3, 'an octave up: ' + up);
  assert.ok(Math.abs(back - 57) < 0.3, 'back to the note: ' + back);
});
