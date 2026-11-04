import test from 'node:test';
import assert from 'node:assert/strict';
import { noteAbove, withNoteAbove } from '../block-notes.mjs';

const song = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\n# the groove\n# keep it simple\ndrums beat at 1 for 2 {\n}\nbass line at 1 for 2 {\n}\n';

test('the note of a block is the comment right above its header', () => {
  assert.equal(noteAbove(song, 4), 'the groove\nkeep it simple');
  assert.equal(noteAbove(song, 6), '');   // the closing brace of the block above is not a comment
  assert.equal(noteAbove(song, 0), '');
});

test('writing a note replaces the comment above, adds one where there was none and removes it when empty', () => {
  const changed = withNoteAbove(song, 4, 'a new\n\nthought');
  assert.match(changed, /# a new\n#\n# thought\ndrums beat/);
  assert.equal(noteAbove(changed, changed.split('\n').findIndex((l) => l.startsWith('drums'))), 'a new\n\nthought');
  const added = withNoteAbove(song, 6, 'bass note');
  assert.match(added, /}\n# bass note\nbass line/);
  const gone = withNoteAbove(song, 4, '');
  assert.doesNotMatch(gone, /the groove/);
  assert.match(gone, /\n\ndrums beat/);
});

test('the view lines of the canvas are not notes', () => {
  const text = 'drums a {\n}\n# view group {"id":"g"}\nbass b {\n}\n';
  assert.equal(noteAbove(text, 3), '');
});
