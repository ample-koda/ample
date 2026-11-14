import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DEFAULT_LIMITS } from '../lite-paths.mjs';
import { createImporter } from '../lite-import.js';

// The importer reads the page through document.querySelector; these are the few elements it uses.
function fakePage() {
  const elements = new Map();
  const element = (selector) => {
    if (!elements.has(selector)) {
      const el = { textContent: '', value: 0, disabled: false, open: false, hidden: false, onclick: null, oncancel: null };
      el.showModal = () => { el.open = true; };
      el.close = () => { el.open = false; };
      el.click = () => el.onclick?.();
      elements.set(selector, el);
    }
    return elements.get(selector);
  };
  globalThis.document = { querySelector: element, addEventListener() {} };
  return element;
}

function rig({ onWrite = async () => {} } = {}) {
  const page = fakePage();
  const files = new Map();
  const log = { toasts: [], added: [], deleted: [] };
  const store = {
    async writeFile(id, path, body) { await onWrite(path, page); files.set(path, body); },
    async deleteFile(id, path) { log.deleted.push(path); files.delete(path); },
  };
  const importer = createImporter({
    project: () => ({ id: 'p1', name: 'Mine' }),
    store: () => store,
    limits: () => DEFAULT_LIMITS,
    taken: () => new Set(),
    targetFor: (path) => path,
    added: async (entries) => { log.added.push(entries.map((e) => e.path)); },
    openAsProject: async () => {},
    snapshot: async () => [],
    toast: (text, kind) => log.toasts.push([text, kind ?? '']),
    isText: (path) => /\.(amplet|txt)$/i.test(path),
  });
  const file = (name, text = 'x') => ({ name, size: text.length, text: async () => text });
  return { importer, files, log, page, file };
}

test('a batch that is not cancelled stays in the project', async () => {
  const r = rig();
  await r.importer.pick([r.file('a.txt'), r.file('b.txt')]);
  assert.deepEqual([...r.files.keys()], ['a.txt', 'b.txt']);
  assert.deepEqual(r.log.deleted, []);
});

test('a Cancel pressed while the only file is being saved removes it again (issue 69)', async () => {
  const r = rig({ onWrite: async (path, page) => { page('#lite-import-cancel').click(); await Promise.resolve(); } });
  await r.importer.pick([r.file('a.txt')]);
  assert.deepEqual([...r.files.keys()], [], 'the project is as it was');
  assert.deepEqual(r.log.deleted, ['a.txt']);
  assert.ok(r.log.toasts.some(([text]) => /Cancelled: nothing was added/.test(text)));
  assert.ok(!r.log.toasts.some(([text]) => /file added|files added/.test(text)), 'no success message');
});

test('a Cancel pressed during the last of several writes removes the whole batch', async () => {
  const r = rig({ onWrite: async (path, page) => { if (path === 'c.txt') page('#lite-import-cancel').click(); } });
  await r.importer.pick([r.file('a.txt'), r.file('b.txt'), r.file('c.txt')]);
  assert.deepEqual([...r.files.keys()], []);
  assert.deepEqual(r.log.deleted.sort(), ['a.txt', 'b.txt', 'c.txt']);
});

test('a Cancel pressed before the second file leaves nothing behind', async () => {
  const r = rig({ onWrite: async (path, page) => { if (path === 'a.txt') page('#lite-import-cancel').click(); } });
  await r.importer.pick([r.file('a.txt'), r.file('b.txt')]);
  assert.deepEqual([...r.files.keys()], []);
});
