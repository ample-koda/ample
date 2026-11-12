import test from 'node:test';
import assert from 'node:assert/strict';
import { currentRowIndex } from '../../app/lyrics-model.js';

const rows = [{ tag: true, text: '[Verse]' }, { bar: 3, text: 'a' }, { bar: 5, text: 'b' }, { tag: true, text: '[Chorus]' }, { bar: 9.5, text: 'c' }];

test('the sung line is the last one that has started', () => {
  assert.equal(currentRowIndex(rows, 1), -1);
  assert.equal(currentRowIndex(rows, 3), 1);
  assert.equal(currentRowIndex(rows, 4.99), 1);
  assert.equal(currentRowIndex(rows, 5), 2);
  assert.equal(currentRowIndex(rows, 9.4), 2);
  assert.equal(currentRowIndex(rows, 40), 4);
});
test('tags are never the current line, and an empty list has none', () => {
  assert.equal(currentRowIndex([{ tag: true, text: '[Verse]' }], 10), -1);
  assert.equal(currentRowIndex([], 3), -1);
});
