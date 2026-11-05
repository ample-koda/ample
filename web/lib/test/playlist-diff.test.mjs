import test from 'node:test';
import assert from 'node:assert/strict';
import { lastChange } from '../playlist-diff.mjs';

test('the last change is the lines between the common start and the common end of the two texts', () => {
  const a = 'one\ntwo\nthree\nfour';
  assert.equal(lastChange(a, a), null);
  assert.deepEqual(lastChange(a, 'one\n2\nthree\nfour'), { line: 2, removed: ['two'], added: ['2'] });
  assert.deepEqual(lastChange(a, 'one\ntwo\nthree\nfour\nfive'), { line: 5, removed: [], added: ['five'] });
  assert.deepEqual(lastChange(a, 'one\nfour'), { line: 2, removed: ['two', 'three'], added: [] });
});

test('a long change is cut, with a note of how many lines were left out', () => {
  const many = Array.from({ length: 30 }, (_, i) => 'x' + i).join('\n');
  const change = lastChange('a', 'a\n' + many, 5);
  assert.equal(change.added.length, 6);
  assert.match(change.added.at(-1), /25 more lines/);
});
