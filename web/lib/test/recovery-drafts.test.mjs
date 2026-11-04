import test from 'node:test';
import assert from 'node:assert/strict';
import { getRecoveryDraft, listRecoveryDrafts, removeRecoveryDraft, saveRecoveryDraft } from '../recovery-drafts.mjs';

class MemoryStorage {
  values = new Map();
  get length() { return this.values.size; }
  key(index) { return [...this.values.keys()][index] ?? null; }
  getItem(key) { return this.values.get(key) ?? null; }
  setItem(key, value) { this.values.set(key, value); }
  removeItem(key) { this.values.delete(key); }
}

test('recovery drafts remain independent per project and file and can be removed', () => {
  const storage = new MemoryStorage();
  saveRecoveryDraft(storage, 'project-a', 'song.amplet', 'unsaved song', 'saved song', 10);
  saveRecoveryDraft(storage, 'project-a', 'lyrics.lrc', 'unsaved words', 'saved words', 20);
  saveRecoveryDraft(storage, 'project-b', 'song.amplet', 'other project', 'other saved project', 30);

  assert.deepEqual(listRecoveryDrafts(storage, 'project-a').map((draft) => draft.path), ['lyrics.lrc', 'song.amplet']);
  assert.equal(getRecoveryDraft(storage, 'project-a', 'song.amplet').text, 'unsaved song');
  assert.equal(getRecoveryDraft(storage, 'project-a', 'song.amplet').baseText, 'saved song');
  assert.equal(getRecoveryDraft(storage, 'project-a', 'missing.amplet'), null);
  removeRecoveryDraft(storage, 'project-a', 'lyrics.lrc');
  assert.deepEqual(listRecoveryDrafts(storage, 'project-a').map((draft) => draft.path), ['song.amplet']);
});

test('recovery reader rejects malformed or mismatched drafts', () => {
  const storage = new MemoryStorage();
  storage.setItem('ample.source-draft:p:file.txt', '{broken');
  assert.equal(getRecoveryDraft(storage, 'p', 'file.txt'), null);
  storage.setItem('ample.source-draft:p:file.txt', JSON.stringify({ path: 'other.txt', text: 'no', updated: 1 }));
  assert.equal(getRecoveryDraft(storage, 'p', 'file.txt'), null);
});
