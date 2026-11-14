import test from 'node:test';
import assert from 'node:assert/strict';
import { canKeepAsUnsaved, isDeleted, splitChanges } from '../koda-changes.mjs';

const change = (path, status = 'modified', extra = {}) => ({ path, status, ...extra });

test('a deletion is the status "deleted" or the old flag', () => {
  assert.equal(isDeleted(change('a.txt', 'deleted')), true);
  assert.equal(isDeleted(change('a.txt', 'modified', { deleted: true })), true);
  assert.equal(isDeleted(change('a.txt', 'added')), false);
  assert.equal(isDeleted(undefined), false);
});

test('deleted files are split from the other files, and the open song is in neither (issue 217)', () => {
  const split = splitChanges([change('song.amplet'), change('notes.txt', 'deleted'), change('a.amplec', 'added'), change('')], 'song.amplet');
  assert.deepEqual(split.others.map((c) => c.path), ['a.amplec']);
  assert.deepEqual(split.deleted.map((c) => c.path), ['notes.txt']);
});

test('a deleted file is never one of the files that come in as unsaved text', () => {
  const split = splitChanges([change('notes.txt', 'deleted')], 'song.amplet');
  assert.equal(split.others.length, 0);
  assert.equal(canKeepAsUnsaved(split), false);
});

test('a proposal that deletes something goes through Accept even when it also adds a component', () => {
  const split = splitChanges([change('a.amplec', 'added'), change('old.txt', 'deleted')], 'song.amplet');
  assert.equal(canKeepAsUnsaved(split), false);
});

test('other files without a deletion or another song can come in as unsaved text', () => {
  assert.equal(canKeepAsUnsaved(splitChanges([change('a.amplec', 'added'), change('song.amplet')], 'song.amplet')), true);
  assert.equal(canKeepAsUnsaved(splitChanges([change('other.amplet', 'added')], 'song.amplet')), false);
  assert.equal(canKeepAsUnsaved(splitChanges([change('song.amplet')], 'song.amplet')), false);
  assert.equal(canKeepAsUnsaved(splitChanges(undefined, 'song.amplet')), false);
});
