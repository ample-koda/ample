import test from 'node:test';
import assert from 'node:assert/strict';
import { formant, llamaCurve, llamaNotes, llamaSettings, renderLlama } from '../clientside-delay-llama.mjs';
import { detectPitch } from '../clientside-autotune.mjs';

const RATE = 22050;
// the note name of the pitch that is sung (a formant voice has a weak fundamental: the octave is not compared)
const pitchClass = (x, from, to) => { const v = [...detectPitch(x.subarray(from, to), RATE).midi].filter((m) => !Number.isNaN(m)).sort((a, b) => a - b); return ((v[v.length >> 1] % 12) + 12) % 12; };
const off = (a, b) => Math.abs((((a - b) % 12) + 18) % 12 - 6);

test('a note is sung at its pitch at a healthy level, like the engine', () => {
  const { left } = renderLlama([{ start: 0, length: RATE * 2, pitch: 57, velocity: 100 }], { vibrato: 0 }, RATE, RATE * 2, 1);
  assert.ok(off(pitchClass(left, RATE / 2, RATE * 3 / 2), 57 % 12) < 0.15);
  const peak = left.reduce((m, v) => Math.max(m, Math.abs(v)), 0);
  assert.ok(peak > 0.05 && peak < 0.95, `peak ${peak}`);
});

test('the vowel table, the settings and the curves read like the engine', () => {
  assert.ok(Math.abs(formant(1, 0.5) - 2140) < 1e-9 && Math.abs(formant(1, 1) - 600) < 1e-9 && formant(0, 0) > formant(0, 0.5));
  const s = llamaSettings({ vowel: 'o', unison: '3', glide: '120 ms', echo: '20%' });
  assert.equal(s.vowel, 0.75); assert.equal(s.unison, 3); assert.equal(s.glideMs, 120); assert.equal(s.echo, 0.2);
  const curve = llamaCurve('auto(1:0, 3:100)', 1, 1000, 3000);
  assert.equal(curve[0], 0); assert.equal(curve[1000], 0.5); assert.equal(curve[2999], 1);
  assert.deepEqual(llamaNotes([{ bar: 2, beat: 1, beats: 2, pitch: 60, velocity: 90 }], 1, 1000)[0], { start: 1250, length: 500, pitch: 60, velocity: 90, fine: 0 });
});

test('the llama hears what is sung (for the picture)', () => {
  const seen = [];
  renderLlama([{ start: 0, length: RATE, pitch: 62, velocity: 100 }], { vibrato: 0 }, RATE, RATE, 1, {}, (sample, pitch, vowel, mouth, level) => seen.push({ pitch, level }));
  assert.ok(seen.some((s) => s.pitch !== null && Math.abs(s.pitch - 62) < 0.2 && s.level > 0.3));
});
