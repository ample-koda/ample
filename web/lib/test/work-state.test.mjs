import test from 'node:test';
import assert from 'node:assert/strict';
import { changedLines, decide, parseRecord, trimHistory, workRecord } from '../work-state.mjs';

test('the history is trimmed to the newest entries that fit', () => {
  const entries = ['a', 'b', 'c', 'd', 'e'];
  assert.deepEqual(trimHistory(entries, 3), ['c', 'd', 'e']);
  assert.deepEqual(trimHistory(['xxxx', 'yy', 'z'], 10, 3), ['yy', 'z']);   // 'yy' and 'z' make 3 characters, the one before is too big
  assert.deepEqual(trimHistory(['a', { object: true }, 'b']), ['a', 'b']);
});

test('a record is read back for its path only', () => {
  const record = workRecord({ project: 'p', path: 'song.amplet', text: 'now', undo: ['one', 'two'], redo: ['three'], now: 5 });
  const back = parseRecord(JSON.stringify(record), 'song.amplet');
  assert.equal(back.text, 'now');
  assert.deepEqual(back.undo, ['one', 'two']);
  assert.equal(parseRecord(JSON.stringify(record), 'other.amplet'), null);
  assert.equal(parseRecord('{broken', 'song.amplet'), null);
  assert.equal(workRecord({ path: 'x', text: undefined }), null);
});

test('lines that changed are counted, and the choice depends on whether the text is the same', () => {
  assert.equal(changedLines('a\nb\nc', 'a\nb\nc'), 0);
  assert.equal(changedLines('a\nb\nc', 'a\nB\nc'), 2);   // one removed and one added
  assert.equal(changedLines('a\nb', 'a\nb\nc\nd'), 2);
  const record = { text: 'work', undo: [], redo: [] };
  assert.equal(decide(null, 'x'), 'none');
  assert.equal(decide(record, 'work'), 'history');
  assert.equal(decide(record, 'saved'), 'ask');
  assert.equal(decide(record, 'saved', true), 'none');
});
