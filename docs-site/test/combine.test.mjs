// node --test docs-site/test/combine.test.mjs : combineAmplets on two real sample codes
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const { combineAmplets } = require('../lib/amplet.js');
const samples = require('../samples.json');
const all = Array.isArray(samples) ? samples : Object.values(samples);

const gainDb = 20 * Math.log10(0.85 / Math.pow(2, 0.75));
const asLayer = (s) => ({ code: s.code, bpm: s.bpm, bars: s.bars, seconds: s.seconds, title: s.title, gainDb });

test('two 120 bpm sounds (a melody and drums) become one song', () => {
  const tone = all.find((s) => s.bpm === 120 && s.seconds === 4 && /midi-clip/.test(s.code) && !/drums/.test(s.code));
  const beat = all.find((s) => s.bpm === 120 && /^drums /m.test(s.code));
  assert.ok(tone && beat, 'samples found');
  const song = combineAmplets([asLayer(tone), asLayer(beat)]);
  assert.equal((song.match(/^composition /gm) || []).length, 1);
  assert.match(song, /tempo 120 /);
  assert.match(song, /master-seed 20261017/);
  assert.equal((song.match(/^master /gm) || []).length, 1);
  assert.match(song, /^volume l1_level \{/m);
  assert.match(song, /^volume l2_level \{/m);
  assert.match(song, /^mixer layers \{/m);
  assert.match(song, /from l1_level\.audio -> audio1/);
  assert.match(song, /from l2_level\.audio -> audio2/);
  assert.match(song, new RegExp('gain ' + gainDb.toFixed(2).replace('-', '\\-') + ' dB'));
  // every block name is prefixed and every from line points at a prefixed block (or a known one)
  const names = [...song.matchAll(/^[a-z][\w-]* ([A-Za-z_]\w*)/gm)].map((m) => m[1]).filter((n) => n !== 'out');
  for (const n of names) assert.match(n, /^(l\d+_|layers$)/, n);
  for (const m of song.matchAll(/from ([A-Za-z_]\w*)\./g)) assert.ok(names.includes(m[1]), 'from ' + m[1]);
  // both layers are 4 s = 2 bars at 120 bpm: the song is one loop of them
  assert.match(song, /at 1 for 2/);
  assert.doesNotMatch(song, /at 1 for 4\b/);
});

test('a 60 bpm melody layer is rescaled to 120 bpm', () => {
  const slow = all.find((s) => s.bpm === 60 && /melody "/.test(s.code));
  const fast = all.find((s) => s.bpm === 120 && /midi-clip/.test(s.code));
  assert.ok(slow && fast);
  const song = combineAmplets([asLayer(fast), asLayer(slow)]);
  assert.match(song, /tempo 120 /);
  assert.doesNotMatch(song, /keeps its own steps/);
  const mel = /^\s*melody "([^"]*)"/m.exec(song.split('volume l1_level')[1]);
  const beats = mel[1].trim().split(/\s+/).reduce((a, t) => a + +t.slice(t.lastIndexOf(':') + 1), 0);
  assert.equal(beats, 16); // 4 bars of 4 beats
});

test('a drum layer that must be slower keeps its speed with fewer steps in a bar', () => {
  // the song runs at 240 (the ticks); the drums of 120 get steps 8 (a bar is half as long, so a step lasts as long as it did)
  const beat = all.find((s) => s.bpm === 120 && /^drums /m.test(s.code));
  const ticks = all.find((s) => s.bpm === 240 && /^midi-clip /m.test(s.code));
  assert.ok(beat && ticks, 'samples found');
  const song = combineAmplets([asLayer(beat), asLayer(ticks)]);
  assert.match(song, /tempo 240 /);
  assert.match(song, /^ {2}steps 8$/m);
  assert.doesNotMatch(song, /stretch(0.5)/);   // the faster layer is not sped up: the song runs at its tempo
});
