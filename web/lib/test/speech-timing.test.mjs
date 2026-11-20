import test from 'node:test';
import assert from 'node:assert/strict';
import { activeWordsAt, parseSpeechTiming, parseSpeechWords, rawSpan, sampleAt, wordAtSample, wordSeconds } from '../speech-timing.mjs';

const take = JSON.stringify({ speech: { mode: 'words', words: [
  { Index: 1, StartSample: 44100, EndSample: 88200, Text: 'two' },
  { Index: 0, StartSample: 0, EndSample: 44100, Text: 'one' },
  { Index: 2, StartSample: 70000, EndSample: 120000, Text: 'three' },
] } });

test('the words are read in the order of the take, with either spelling of the field names', () => {
  const words = parseSpeechWords(take);
  assert.deepEqual(words.map((w) => w.index), [0, 1, 2]);
  assert.equal(words[1].text, 'two');
  const camel = parseSpeechWords({ speech: { words: [{ index: 3, startSample: 5, endSample: 9, text: 'x' }] } });
  assert.deepEqual(camel, [{ index: 3, start: 5, end: 9, text: 'x', from: null, to: null }]);
});

test('anything that is not a list of intervals gives no words and never throws', () => {
  for (const bad of [null, undefined, '', 'not json', '{}', '{"speech":{}}', { speech: { words: 'x' } }, { speech: { words: [null, 7, {}] } }]) {
    assert.deepEqual(parseSpeechWords(bad), []);
  }
  // an empty, negative or reversed interval is dropped
  const dropped = parseSpeechWords({ speech: { words: [
    { index: 0, startSample: 10, endSample: 10 }, { index: 1, startSample: -5, endSample: 5 }, { index: 2, startSample: 9, endSample: 4 }, { index: 3, startSample: 1, endSample: 2 },
  ] } });
  assert.deepEqual(dropped.map((w) => w.index), [3]);
});

test('intervals are half-open: a word that ends at the sample has stopped and one that starts at it has started', () => {
  const words = parseSpeechWords(take);
  assert.equal(wordAtSample(words, 0), 0);
  assert.equal(wordAtSample(words, 44099), 0);
  assert.equal(wordAtSample(words, 44100), 1);
  assert.equal(wordAtSample(words, 88199), 2);   // word 1 ends at 88200 but word 2 started at 70000: the one that started last
  assert.equal(wordAtSample(words, 88200), 2);
  assert.equal(wordAtSample(words, 120000), -1);
  assert.equal(wordAtSample(words, -1), -1);
});

test('overlapping voices give every sounding word, the latest first', () => {
  const words = parseSpeechWords(take);
  assert.deepEqual(activeWordsAt(words, 75000), [2, 1]);
  assert.deepEqual(activeWordsAt(words, 100000), [2]);
  assert.deepEqual(activeWordsAt(words, 500000), []);
  assert.deepEqual(activeWordsAt(null, 5), []);
});

test('a playback position becomes a sample of the take, with the offset of a range build', () => {
  assert.equal(sampleAt(1, 44100), 44100);
  assert.equal(sampleAt(0.5, 44100, 1000), 23050);
  assert.equal(sampleAt(NaN, 44100), -1);
  assert.equal(sampleAt(1, 0), -1);
  // the word at the playback position of a sound that starts at sample 44100 of the take (a range from the second second)
  const words = parseSpeechWords(take);
  assert.equal(wordAtSample(words, sampleAt(0, 44100, 44100)), 1);
});

test('the seconds of a word are in the time of the sound that plays', () => {
  const words = parseSpeechWords(take);
  assert.deepEqual(wordSeconds(words, 1, 44100), { start: 1, end: 2 });
  assert.deepEqual(wordSeconds(words, 1, 44100, 44100), { start: 0, end: 1 });
  assert.equal(wordSeconds(words, 9, 44100), null);
  assert.equal(wordSeconds(words, 1, 0), null);
});

test('the take bank keeps the metadata under the name of the output: that form is read too, with the rate and the places in the text', () => {
  const kept = JSON.stringify({ audio: { speech: { sampleRate: 44100, words: [
    { Index: 0, StartSample: 0, EndSample: 100, Text: 'go', From: 0, To: 2 },
    { Index: 1, StartSample: 100, EndSample: 300, Text: 'four', From: 3, To: 5 },
    { Index: 2, StartSample: 300, EndSample: 400, Text: 'x', From: 7, To: 7 },
  ] } } });
  const timing = parseSpeechTiming(kept);
  assert.equal(timing.sampleRate, 44100);
  assert.deepEqual(timing.words.map((w) => [w.index, w.from, w.to]), [[0, 0, 2], [1, 3, 5], [2, null, null]]);   // an empty place is no place
  assert.equal(parseSpeechTiming('{}').sampleRate, null);
});

test('a place in the decoded text becomes a place in the written string, escapes included', () => {
  assert.deepEqual(rawSpan('Go now', 3, 6), { from: 3, to: 6 });
  // written: say \"hi\" now   (decoded: say "hi" now)
  const written = 'say \\"hi\\" now';
  assert.deepEqual(rawSpan(written, 5, 7), { from: 6, to: 8 });         // hi
  assert.deepEqual(rawSpan(written, 4, 8), { from: 4, to: 10 });        // "hi" with both escaped quotes
  assert.deepEqual(rawSpan('a\\nb', 2, 3), { from: 3, to: 4 });          // the b after a backslash-n
  assert.equal(rawSpan('abc', 2, 9), null);
  assert.equal(rawSpan('abc', 2, 2), null);
  assert.equal(rawSpan('abc', -1, 2), null);
});
