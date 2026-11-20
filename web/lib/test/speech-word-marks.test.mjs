import test from 'node:test';
import assert from 'node:assert/strict';
import { speechWordMarks } from '../listen-highlight.mjs';

const SONG = 'composition "t" { ample 1 tempo 120 master-seed 1 }\n\nspeech voice at 1 for 2 {\n  text "Go 42, now"\n}\n\nmidi-clip m at 1 for 1 {\n  notes "1:0:1:60:100"\n}\n';
const WORDS = [
  { index: 0, start: 0, end: 1000, from: 0, to: 2 },       // Go
  { index: 1, start: 1000, end: 2000, from: 3, to: 5 },    // four (the number 42 ...)
  { index: 2, start: 2000, end: 3000, from: 3, to: 5 },    // two (... shares its place)
  { index: 3, start: 3000, end: 4000, from: 7, to: 10 },   // now
];
const marksAt = (over) => speechWordMarks({ text: SONG, name: 'voice', words: WORDS, sampleRate: 1000, takeSeconds: 0.5, ...over });

test('the speech word that sounds at the playhead is marked in the written text of its block', () => {
  const marks = marksAt({ takeSeconds: 3.2 });
  assert.equal(marks.length, 1);
  assert.equal(SONG.slice(marks[0].from, marks[0].to), 'now');
  assert.equal(marks[0].cls, 'lp-now');
  const first = marksAt({ takeSeconds: 0 });
  assert.equal(SONG.slice(first[0].from, first[0].to), 'Go');
  const number = marksAt({ takeSeconds: 2.5 });   // the digits of a number light up the whole number
  assert.equal(SONG.slice(number[0].from, number[0].to), '42');
});

test('nothing is marked when no word sounds, for another block type, a missing block, a text that is not one string or a word without a place', () => {
  assert.equal(marksAt({ takeSeconds: 9 }).length, 0);
  assert.equal(marksAt({ name: 'm' }).length, 0);
  assert.equal(marksAt({ name: 'nobody' }).length, 0);
  assert.equal(marksAt({ text: SONG.replace('text "Go 42, now"', 'text Go') }).length, 0);
  assert.equal(marksAt({ words: [{ index: 0, start: 0, end: 1000, from: null, to: null }] }).length, 0);
  assert.equal(marksAt({ takeSeconds: Number.NaN }).length, 0);
});

test('escapes in the written text move the mark to the right characters', () => {
  const text = 'composition "t" { ample 1 }\nspeech v at 1 for 1 {\n  text "say \\"hi\\" now"\n}\n';
  // decoded: say "hi" now; the word hi is at 5..7
  const marks = speechWordMarks({ text, name: 'v', words: [{ index: 0, start: 0, end: 10, from: 5, to: 7 }], sampleRate: 1000, takeSeconds: 0.001 });
  assert.equal(text.slice(marks[0].from, marks[0].to), 'hi');
});
