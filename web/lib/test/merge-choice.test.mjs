import test from 'node:test';
import assert from 'node:assert/strict';
import { compose, conflictCount } from '../merge-choice.mjs';

const regions = [
  { conflict: false, common: ['head', '# one'] },
  { conflict: true, base: ['# two'], mine: ['# mine'], theirs: ['# theirs'] },
  { conflict: false, common: ['# three'] },
  { conflict: true, base: [], mine: ['x'], theirs: [] },
];

test('every region is counted and the default keeps the working copy', () => {
  assert.equal(conflictCount(regions), 2);
  assert.equal(compose(regions), 'head\n# one\n# mine\n# three\nx\n');
});

test('A takes the file, both puts A before B, custom takes the typed text', () => {
  assert.equal(compose(regions, { 1: { mode: 'theirs' }, 3: { mode: 'theirs' } }), 'head\n# one\n# theirs\n# three\n');
  assert.equal(compose(regions, { 1: { mode: 'both' }, 3: { mode: 'mine' } }), 'head\n# one\n# theirs\n# mine\n# three\nx\n');
  assert.equal(compose(regions, { 1: { mode: 'custom', custom: 'my own\nsecond\n' }, 3: { mode: 'theirs' } }), 'head\n# one\nmy own\nsecond\n# three\n');
  assert.equal(compose(regions, { 1: { mode: 'custom', custom: '' }, 3: { mode: 'theirs' } }), 'head\n# one\n# three\n');
});

test('no regions is an empty text', () => {
  assert.equal(compose([]), '');
});
