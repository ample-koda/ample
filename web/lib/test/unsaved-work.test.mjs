import test from 'node:test';
import assert from 'node:assert/strict';
import { stillUnsaved, unsavedMessage, unsavedWork } from '../unsaved-work.mjs';

test('nothing unsaved gives an empty list', () => {
  assert.deepEqual(unsavedWork(), []);
  assert.deepEqual(unsavedWork({ dirtyTabs: [], drafts: [] }), []);
});

test('a changed song in a background tab counts, not only the song that has focus', () => {
  const items = unsavedWork({ dirtyTabs: ['first.amplet'] });
  assert.deepEqual(items.map((i) => i.path), ['first.amplet']);
  assert.deepEqual(items[0].reasons, ['changed in an open tab']);
});

test('modules and text files count like songs', () => {
  const items = unsavedWork({ dirtyTabs: ['modules/a.module', 'notes.txt', 'b.amplet'] });
  assert.deepEqual(items.map((i) => i.path), ['b.amplet', 'modules/a.module', 'notes.txt']);
});

test('a recovery buffer counts only when its text differs from its base text', () => {
  const items = unsavedWork({
    drafts: [
      { path: 'same.txt', text: 'a', baseText: 'a' },
      { path: 'changed.txt', text: 'b', baseText: 'a' },
      { path: 'broken.txt', text: 5, baseText: 'a' },
      null,
    ],
  });
  assert.deepEqual(items.map((i) => i.path), ['changed.txt']);
  assert.deepEqual(items[0].reasons, ['unsaved text kept in this browser']);
});

test('a path found in several places is listed once, with every reason', () => {
  const items = unsavedWork({ dirtyTabs: ['x.amplet'], drafts: [{ path: 'x.amplet', text: 'b', baseText: 'a' }] });
  assert.equal(items.length, 1);
  assert.deepEqual(items[0].reasons.sort(), ['changed in an open tab', 'unsaved text kept in this browser']);
});

test('the list is sorted and junk paths are ignored', () => {
  const items = unsavedWork({ dirtyTabs: ['b', '', undefined, 'a', 7] });
  assert.deepEqual(items.map((i) => i.path), ['a', 'b']);
});

test('the question names the files, with their folders left out', () => {
  assert.equal(unsavedMessage([{ path: 'songs/one.amplet', reasons: [] }]),
    '1 file has changes that are not saved: one.amplet. Going to another branch leaves them behind.');
  assert.equal(unsavedMessage([{ path: 'a.amplet', reasons: [] }, { path: 'modules/b.module', reasons: [] }], 'Nothing was switched.'),
    '2 files have changes that are not saved: a.amplet, b.module. Nothing was switched.');
});

test('a long list is shortened to six names and a count', () => {
  const items = ['a', 'b', 'c', 'd', 'e', 'f', 'g', 'h'].map((n) => ({ path: `${n}.txt`, reasons: [] }));
  const text = unsavedMessage(items);
  assert.match(text, /^8 files have changes that are not saved: a\.txt, b\.txt, c\.txt, d\.txt, e\.txt, f\.txt and 2 more\./);
});

test('after a save the switch may only go on when nothing is left and nothing failed', () => {
  assert.deepEqual(stillUnsaved([], []), []);
  assert.deepEqual(stillUnsaved([{ path: 'a', reasons: ['x'] }], []).map((i) => i.path), ['a']);
  const left = stillUnsaved([], ['broken.amplet']);
  assert.deepEqual(left.map((i) => i.path), ['broken.amplet']);
  assert.deepEqual(left[0].reasons, ['the save failed']);
  const both = stillUnsaved([{ path: 'a', reasons: ['x'] }, { path: 'b', reasons: ['y'] }], ['b', 'c']);
  assert.deepEqual(both.map((i) => i.path), ['a', 'b', 'c']);
});
