import test from 'node:test';
import assert from 'node:assert/strict';
import { diffLines, merge3, splitLines } from '../three-way.mjs';

const base = ['a', 'b', 'c', 'd', 'e', 'f'].join('\n');

test('diffLines finds the changed lines and nothing else', () => {
  assert.deepEqual(diffLines(splitLines(base), splitLines(base)), []);
  assert.deepEqual(diffLines(['a', 'b', 'c'], ['a', 'x', 'c']), [{ a0: 1, a1: 2, b0: 1, b1: 2 }]);
  assert.deepEqual(diffLines(['a', 'b'], ['a', 'b', 'c']), [{ a0: 2, a1: 2, b0: 2, b1: 3 }]);
  assert.equal(diffLines(['a', 'b', 'c', 'd'], ['b', 'c', 'x', 'd']).length, 2);
});

test('changes in different places merge by themselves', () => {
  const mine = ['a', 'B', 'c', 'd', 'e', 'f'].join('\n');
  const theirs = ['a', 'b', 'c', 'd', 'e', 'F', 'g'].join('\n');
  const merged = merge3(base, mine, theirs);
  assert.equal(merged.conflicts, 0);
  assert.equal(merged.text, ['a', 'B', 'c', 'd', 'e', 'F', 'g'].join('\n'));
});

test('the same change on both sides is no conflict', () => {
  const both = ['a', 'b', 'X', 'd', 'e', 'f'].join('\n');
  const merged = merge3(base, both, both);
  assert.equal(merged.conflicts, 0);
  assert.equal(merged.text, both);
});

test('different changes of the same line, or of touching lines, are a conflict that keeps my lines', () => {
  const mine = ['a', 'b', 'MINE', 'd', 'e', 'f'].join('\n');
  const theirs = ['a', 'b', 'THEIRS', 'd', 'e', 'f'].join('\n');
  const merged = merge3(base, mine, theirs);
  assert.equal(merged.conflicts, 1);
  assert.equal(merged.text, mine);
  const touching = merge3(base, ['a', 'b', 'M', 'd', 'e', 'f'].join('\n'), ['a', 'b', 'c', 'T', 'e', 'f'].join('\n'));
  assert.equal(touching.conflicts, 1);
});

test('nothing typed means the song as it is now', () => {
  const theirs = ['a', 'b', 'c', 'D', 'e', 'f'].join('\n');
  assert.equal(merge3(base, base, theirs).text, theirs);
  assert.equal(merge3(base, theirs, base).text, theirs);
});
