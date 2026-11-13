import test from 'node:test';
import assert from 'node:assert/strict';
import { readNotes } from '../notes-load.mjs';

test('out-of-order notes replies stay with their own song', async () => {
  const waiting = {};
  const get = (url) => new Promise((resolve) => { waiting[/path=([^&]*)/.exec(url)[1]] = resolve; });
  const storage = { getItem: () => null };
  const S = { notes: null }; let gen = 0;
  const open = async (name) => {
    const mine = ++gen;
    const r = await readNotes({ get, file: `notes/${name}.json`, legacyKey: name, storage });
    if (mine === gen) S.notes = r.notes;   // the same guard openDoc uses
  };
  const a = open('A'); await new Promise((r) => setImmediate(r));
  const b = open('B'); await new Promise((r) => setImmediate(r));
  waiting[encodeURIComponent('notes/B.json')]({ text: '{"b":1}' });
  await b;
  waiting[encodeURIComponent('notes/A.json')]({ text: '{"a":1}' });
  await a;
  assert.deepEqual(S.notes, { b: 1 });
});

test('legacy browser notes are returned for migration', async () => {
  const r = await readNotes({ get: async () => ({ text: null }), file: 'f', legacyKey: 'k', storage: { getItem: () => '{"x":1}' } });
  assert.deepEqual(r, { notes: { x: 1 }, migrate: true });
});
