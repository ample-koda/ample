import test from 'node:test';
import assert from 'node:assert/strict';
import { movePattern, parseSeq } from '../seq-model.mjs';

const TEXT = 'sequencer 1\nchannel lead notes wave=square\npattern a bars=1\n  lead 1:0:1:60:96\npattern b bars=1\n  lead 1:0:1:62:96\npattern c bars=1\n  lead 1:0:1:64:96\n';
const names = (seq) => seq.patterns.map((p) => p.name);

test('a pattern moves to another place in the row and nothing else changes', () => {
  const { seq } = parseSeq(TEXT);
  const moved = movePattern(seq, 'c', 0);
  assert.deepEqual(names(moved), ['c', 'a', 'b']);
  assert.deepEqual(moved.patterns.find((p) => p.name === 'c').rows, seq.patterns.find((p) => p.name === 'c').rows);
  assert.deepEqual(names(seq), ['a', 'b', 'c']);   // the old song is not changed
});

test('a move to the same place, or past the ends, is clamped', () => {
  const { seq } = parseSeq(TEXT);
  assert.equal(movePattern(seq, 'b', 1), seq);
  assert.deepEqual(names(movePattern(seq, 'a', 99)), ['b', 'c', 'a']);
  assert.equal(movePattern(seq, 'nope', 0), seq);
});

test('a variation cannot be moved before its base, and its base cannot be moved after it', () => {
  const { seq } = parseSeq(TEXT + 'pattern a-b bars=1 from=a\n');
  assert.equal(movePattern(seq, 'a-b', 0), null);
  assert.equal(movePattern(seq, 'a', 3), null);
  assert.deepEqual(names(movePattern(seq, 'a-b', 2)), ['a', 'b', 'a-b', 'c']);
});
