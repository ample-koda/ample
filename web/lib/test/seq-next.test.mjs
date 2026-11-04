import test from 'node:test';
import assert from 'node:assert/strict';
import { addMarker, addVariation, deletePattern, formatSeq, parseSeq, removeMarker, renamePattern, rowsOf, setLoop, setRow, sourceBar, toggleStep, updateMarker } from '../seq-model.mjs';

const BASE = 'sequencer 1\nchannel kick drum sample=samples/kick.wav\nchannel hat drum sample=samples/hat.wav\nchannel gtr guitar set=samples/gtr\npattern verse bars=2\n  kick x...x...x...x...|x...x...x...x...\n  hat x.x.x.x.x.x.x.x.\n  gtr D...D.u.D.u.D.u. chords Am F\nplaylist\n  clip verse track=1 at=1 for=4\n';

test('guitar channels, markers, the loop and variations read and write the same text', () => {
  const text = `${BASE}  marker Drop at=3\n  loop at=2 for=2\n`.replace('playlist\n  clip verse track=1 at=1 for=4\n', 'pattern verse-b bars=2 from=verse\n  kick -\nplaylist\n  clip verse track=1 at=1 for=4\n');
  const { seq, error } = parseSeq(text);
  assert.equal(error, undefined);
  assert.equal(seq.channels[2].type, 'guitar');
  assert.deepEqual(seq.markers, [{ name: 'Drop', at: 3 }]);
  assert.deepEqual(seq.loop, { at: 2, bars: 2 });
  assert.equal(seq.patterns[1].from, 'verse');
  assert.equal(seq.patterns[1].bars, 2);
  assert.equal(formatSeq(seq), text);
});

test('a variation starts from its base, may silence a row and cannot come from nothing or from a variation', () => {
  const { seq } = parseSeq(`${BASE.split('playlist')[0]}pattern verse-b from=verse\n  hat xxxxxxxxxxxxxxxx\n  kick -\nplaylist\n`);
  const rows = rowsOf(seq, seq.patterns[1]);
  assert.equal(rows.hat, 'xxxxxxxxxxxxxxxx');
  assert.equal('kick' in rows, false);
  assert.match(rows.gtr, /chords Am F/);
  assert.match(parseSeq(`${BASE}pattern x from=nope\n`).error, /defined before it/);
  assert.match(parseSeq(`${BASE.split('playlist')[0]}pattern b from=verse\npattern c from=b\n`).error, /itself a variation/);
});

test('the Variation edit makes a pattern that inherits, a step click starts an own row, and deleting the base takes its variations along', () => {
  let { seq } = parseSeq(BASE);
  const made = addVariation(seq, 'verse');
  seq = made.seq;
  assert.equal(made.name, 'verse-b');
  assert.equal(seq.patterns[1].from, 'verse');
  assert.deepEqual(seq.patterns[1].rows, {});
  seq = toggleStep(seq, 'verse-b', 'hat', 0, 1);   // starts from the inherited hat row
  assert.match(seq.patterns[1].rows.hat, /^xxx\.x\.x\.x\.x\.x\.x\./);
  assert.equal(addVariation(seq, 'verse-b').seq.patterns[2].from, 'verse');   // a variation of a variation starts from the plain pattern
  seq = renamePattern(seq, 'verse', 'chorus');
  assert.equal(seq.patterns[1].from, 'chorus');
  seq = setRow(seq, 'verse-b', 'kick', '-');
  assert.equal('kick' in rowsOf(seq, seq.patterns[1]), false);
  const gone = deletePattern(seq, 'chorus');
  assert.deepEqual(gone.patterns, []);
  assert.deepEqual(gone.clips, []);
});

test('markers and the loop are edited without touching their argument, and the loop maps bars', () => {
  const { seq } = parseSeq(BASE);
  const withMarker = addMarker(seq, 'Big Drop', 5);
  assert.deepEqual(withMarker.markers, [{ name: 'Big-Drop', at: 5 }]);
  assert.deepEqual(seq.markers, []);
  assert.deepEqual(updateMarker(withMarker, 0, { at: 9 }).markers[0], { name: 'Big-Drop', at: 9 });
  assert.deepEqual(removeMarker(withMarker, 0).markers, []);
  const looped = setLoop(seq, { at: 2, bars: 2 });
  assert.deepEqual([1, 2, 3, 4, 5, 6].map((b) => sourceBar(looped, b)), [1, 2, 3, 2, 3, 2]);
  assert.equal(setLoop(looped, null).loop, null);
  assert.equal(sourceBar(seq, 7), 7);
});
