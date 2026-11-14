import test from 'node:test';
import assert from 'node:assert/strict';
import { isOpenable } from './file-types.mjs';

test('openable files are matched by their real extension', () => {
  assert.equal(isOpenable('notes/readme.txt'), true);
  assert.equal(isOpenable('a/B.WAV'), true);
  assert.equal(isOpenable('readme.atxt'), false);
  assert.equal(isOpenable('x.module'), false);
  assert.equal(isOpenable('txt'), false);
});
