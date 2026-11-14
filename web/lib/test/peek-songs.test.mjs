import test from 'node:test';
import assert from 'node:assert/strict';
import { peekSongs } from '../peek-songs.mjs';

test('the open song comes first when the revision has it, the others in name order (issue 145)', () => {
  const files = { 'b.amplet': '', 'a.amplet': '', 'c.amplet': '', 'notes.txt': '', 'samples/k.wav': '' };
  assert.deepEqual(peekSongs(files, 'b.amplet'), ['b.amplet', 'a.amplet', 'c.amplet']);
});

test('a revision without the open song lists its own songs', () => {
  assert.deepEqual(peekSongs({ 'b.amplet': '', 'a.AMPLET': '' }, 'new.amplet'), ['a.AMPLET', 'b.amplet']);
});

test('a revision without songs, or without files, has none', () => {
  assert.deepEqual(peekSongs({ 'notes.txt': '' }, 'x.amplet'), []);
  assert.deepEqual(peekSongs(undefined, 'x.amplet'), []);
});
