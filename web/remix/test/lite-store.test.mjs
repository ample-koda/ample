import assert from 'node:assert/strict';
import { test } from 'node:test';
import { memoryKv } from '../lite-kv.mjs';
import { DEFAULT_LIMITS, checkFile, cleanPath } from '../lite-paths.mjs';
import { LiteStoreError, createLiteStore } from '../lite-store.mjs';

let counter = 0;
const make = (limits = DEFAULT_LIMITS, kv = memoryKv()) => ({ kv, store: createLiteStore(kv, { now: () => ++counter, uuid: () => `p${++counter}`, limits }) });

test('a project keeps text and binary files and gives them back', async () => {
  const { store } = make();
  const project = await store.createProject({ name: 'Mine', files: [{ path: 'song.amplet', text: 'composition "x" { ample 1  tempo 100  master-seed 1 }' }, { path: 'samples/kick.wav', blob: new Blob([new Uint8Array([1, 2, 3, 4])], { type: 'audio/wav' }) }] });
  assert.equal((await store.listFiles(project.id)).map((f) => f.path).join(), 'samples/kick.wav,song.amplet');
  assert.equal((await store.readFile(project.id, 'song.amplet')).text, 'composition "x" { ample 1  tempo 100  master-seed 1 }');
  const kick = await store.readFile(project.id, 'samples/kick.wav');
  assert.equal(kick.kind, 'binary');
  assert.equal(kick.size, 4);
  assert.equal(kick.mime, 'audio/wav');
  assert.deepEqual(new Uint8Array(await kick.blob.arrayBuffer()), new Uint8Array([1, 2, 3, 4]));
});

test('changes, new files and deletes survive a new store over the same data (a reload)', async () => {
  const { kv, store } = make();
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'one' }] });
  await store.writeFile(id, 'a.amplet', { text: 'two' });
  await store.writeFile(id, 'components/b.amplec', { text: 'comp' });
  await store.deleteFile(id, 'a.amplet');
  const again = createLiteStore(kv);
  assert.equal((await again.last()), id);
  assert.deepEqual((await again.listFiles(id)).map((f) => f.path), ['components/b.amplec']);
  assert.equal((await again.readFile(id, 'components/b.amplec')).text, 'comp');
});

test('projects are separate: files, folders, rename and delete', async () => {
  const { store } = make();
  const a = await store.createProject({ name: 'a', files: [{ path: 'x/1.txt', text: 'a1' }, { path: 'x/2.txt', text: 'a2' }, { path: 'y.txt', text: 'ay' }] });
  const b = await store.createProject({ name: 'b', files: [{ path: 'x/1.txt', text: 'b1' }] });
  await store.deleteFolder(a.id, 'x');
  assert.deepEqual((await store.listFiles(a.id)).map((f) => f.path), ['y.txt']);
  assert.equal((await store.readFile(b.id, 'x/1.txt')).text, 'b1');
  await store.renameFile(b.id, 'x/1.txt', 'x/one.txt');
  assert.deepEqual((await store.listFiles(b.id)).map((f) => f.path), ['x/one.txt']);
  await assert.rejects(store.renameFile(b.id, 'x/one.txt', 'x/one.txt'), /exists already/);
  await store.deleteProject(a.id);
  assert.deepEqual((await store.listProjects()).map((p) => p.id), [b.id]);
  assert.equal((await store.listFiles(a.id)).length, 0);
});

test('the last project is remembered and forgotten when it is deleted', async () => {
  const { store } = make();
  const a = await store.createProject({ name: 'a' });
  const b = await store.createProject({ name: 'b' });
  assert.equal(await store.last(), b.id);
  await store.setLast(a.id);
  assert.equal(await store.last(), a.id);
  await store.deleteProject(a.id);
  assert.equal(await store.last(), null);
});

test('names, types and sizes are checked before anything is written', async () => {
  const small = { ...DEFAULT_LIMITS, maxFiles: 2, maxTextBytes: 20, maxProjectBytes: 50, maxAudioBytes: 40 };
  const { store } = make(small);
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'short' }] });
  await assert.rejects(store.writeFile(id, 'big.amplet', { text: 'x'.repeat(30) }), /most for this kind/);
  await assert.rejects(store.writeFile(id, 'run.exe', { blob: new Blob([1]) }), /can hold/);
  await assert.rejects(store.writeFile(id, 'clip.mp3', { blob: new Blob([1]) }), /can hold/);
  await assert.rejects(store.writeFile(id, 'c.wav', { text: 'x' }), (e) => e instanceof LiteStoreError && e.code === 'type');
  await assert.rejects(store.writeFile(id, 'b.amplet', { blob: new Blob([1]) }), (e) => e.code === 'type');
  await store.writeFile(id, 'b.wav', { blob: new Blob([new Uint8Array(30)]) });
  await assert.rejects(store.writeFile(id, 'c.wav', { blob: new Blob([new Uint8Array(10)]) }), /at most 2 files/);
  await assert.rejects(store.writeFile(id, 'a.amplet', { text: 'x'.repeat(19) }).then(() => store.writeFile(id, 'b.wav', { blob: new Blob([new Uint8Array(40)]) })), /the most is/);
});

test('a project that cannot be filled is not left behind', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxFiles: 1 });
  await assert.rejects(store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'a' }, { path: 'b.amplet', text: 'b' }] }), /at most 1 files/);
  assert.equal((await store.listProjects()).length, 0);
});

test('hostile names are refused', () => {
  for (const path of ['../evil.amplet', 'a/../../b.amplet', '/etc/passwd', 'C:/x.wav', 'a\\b.amplet', 'a//b.amplet', '.ample/project.json', 'samples/.hidden.wav', 'CON.txt', 'a:b.txt', 'name .txt ', 'a/b/c/d/e/f/g/h.txt', '', 'x'.repeat(81) + '.txt']) {
    assert.throws(() => cleanPath(path), Error, path);
  }
  assert.equal(cleanPath('samples/kit/kick.wav'), 'samples/kit/kick.wav');
  assert.throws(() => checkFile('a.mp3', 1), /can hold/);
  assert.throws(() => checkFile('a.wav', 26 * 1024 * 1024), /most for this kind/);
});

test('the snapshot is every file with its content, ready for a build', async () => {
  const { store } = make();
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'song' }, { path: 's.wav', blob: new Blob([new Uint8Array(3)]) }] });
  const snapshot = await store.snapshot(id);
  assert.equal(snapshot.length, 2);
  assert.equal(snapshot.find((f) => f.path === 'a.amplet').text, 'song');
  assert.equal(snapshot.find((f) => f.path === 's.wav').blob.size, 3);
  assert.deepEqual(await store.usage(id), { files: 2, bytes: 7 });
});

test('reset deletes all Lite data and only that', async () => {
  const lite = memoryKv();
  const other = memoryKv();
  await other.put('ample.studio.setting', 'keep me');
  const { store } = make(DEFAULT_LIMITS, lite);
  await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'x' }] });
  await store.reset();
  assert.deepEqual(await lite.keys(), []);
  assert.equal(await other.get('ample.studio.setting'), 'keep me');
  assert.equal((await createLiteStore(lite).listProjects()).length, 0);
});

test('data from another schema is refused with a clear message so that Reset can be offered', async () => {
  const kv = memoryKv();
  await kv.put('meta:app', { schema: 99, last: null });
  await assert.rejects(createLiteStore(kv).init(), (e) => e instanceof LiteStoreError && e.code === 'schema');
});

import { idbKv } from '../lite-kv.mjs';

test('a browser storage that never answers becomes an error with a hint, not a page that waits for ever', async () => {
  // a database whose transaction never completes (a frozen tab holds it)
  const stuck = { open() { const request = {}; setTimeout(() => { request.result = { objectStoreNames: { contains: () => true }, transaction: () => ({ objectStore: () => ({ put: () => ({}) }) }) }; request.onsuccess?.(); }, 0); return request; } };
  const kv = idbKv(stuck, 'x', { timeoutMs: 80 });
  await assert.rejects(() => kv.put('k', { a: 1 }), /does not answer.*close other Remix tabs/);
  const never = { open() { return {}; } };   // not even the open answers
  await assert.rejects(() => idbKv(never, 'y', { timeoutMs: 80 }).get('k'), /does not answer/);
});

// Limits and kinds under concurrency: a check and the write after it are one step, also between two tabs (a shared Web Lock), and a refused change leaves nothing behind.
const pause = (ms) => new Promise((resolve) => setTimeout(resolve, ms));   // every read and write of the store takes a moment, so that writes can overlap
const slow = (kv, ms = 2) => ({
  ...kv,
  get: async (k) => { await pause(ms); return kv.get(k); },
  put: async (k, v) => { await pause(ms); return kv.put(k, v); },
});
const lockedTabs = async (run) => {
  let chain = Promise.resolve();
  const saved = Object.getOwnPropertyDescriptor(globalThis, 'navigator');
  Object.defineProperty(globalThis, 'navigator', { configurable: true, value: { locks: { request: (name, task) => { const r = chain.then(task); chain = r.catch(() => {}); return r; } } } });
  try { await run(); } finally { if (saved) Object.defineProperty(globalThis, 'navigator', saved); else delete globalThis.navigator; }
};

test('two writes that each fit the file limit but not together: one is accepted, one refused, nothing half-written', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxFiles: 1 }, slow(memoryKv()));
  const { id } = await store.createProject({ name: 'p' });
  const results = await Promise.allSettled([store.writeFile(id, 'a.amplet', { text: 'a' }), store.writeFile(id, 'b.amplet', { text: 'b' })]);
  assert.deepEqual(results.map((r) => r.status).sort(), ['fulfilled', 'rejected']);
  assert.match(results.find((r) => r.status === 'rejected').reason.message, /at most 1 files/);
  assert.equal((await store.listFiles(id)).length, 1);
});

test('two writes that each fit the project size but not together: the second is refused and the first stays whole', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxProjectBytes: 10 }, slow(memoryKv()));
  const { id } = await store.createProject({ name: 'p' });
  const results = await Promise.allSettled([store.writeFile(id, 'a.amplet', { text: '123456' }), store.writeFile(id, 'b.amplet', { text: '123456' })]);
  assert.deepEqual(results.map((r) => r.status), ['fulfilled', 'rejected']);
  assert.deepEqual((await store.listFiles(id)).map((f) => f.path), ['a.amplet']);
});

test('replacing files at the same time is measured against what is there, not against what was there when each started', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxProjectBytes: 10 }, slow(memoryKv()));
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: '1234' }] });
  const results = await Promise.allSettled([store.writeFile(id, 'a.amplet', { text: '12345678' }), store.writeFile(id, 'b.amplet', { text: '123456' })]);
  assert.deepEqual(results.map((r) => r.status), ['fulfilled', 'rejected']);
  assert.equal((await store.readFile(id, 'a.amplet')).text, '12345678');
  assert.equal((await store.listFiles(id)).length, 1);
});

test('a write and a rename at the same time cannot get past the limit', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxFiles: 2 }, slow(memoryKv()));
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'a' }, { path: 'b.amplet', text: 'b' }] });
  const results = await Promise.allSettled([store.renameFile(id, 'a.amplet', 'c.amplet'), store.writeFile(id, 'd.amplet', { text: 'd' })]);
  assert.deepEqual(results.map((r) => r.status), ['fulfilled', 'rejected']);
  assert.deepEqual((await store.listFiles(id)).map((f) => f.path).sort(), ['b.amplet', 'c.amplet']);
});

test('two tabs on the same data share the limit through the Web Lock', async () => {
  await lockedTabs(async () => {
    const kv = slow(memoryKv());
    const one = make({ ...DEFAULT_LIMITS, maxFiles: 1 }, kv).store, two = make({ ...DEFAULT_LIMITS, maxFiles: 1 }, kv).store;
    const { id } = await one.createProject({ name: 'p' });
    const results = await Promise.allSettled([one.writeFile(id, 'a.amplet', { text: 'a' }), two.writeFile(id, 'b.amplet', { text: 'b' })]);
    assert.deepEqual(results.map((r) => r.status).sort(), ['fulfilled', 'rejected']);
    assert.equal((await one.listFiles(id)).length, 1);
  });
});

test('a refused change leaves the project as it was, and the next change still works', async () => {
  const { store } = make({ ...DEFAULT_LIMITS, maxFiles: 1 });
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'a' }] });
  await assert.rejects(store.writeFile(id, 'b.amplet', { text: 'b' }), /at most 1/);
  assert.deepEqual((await store.listFiles(id)).map((f) => f.path), ['a.amplet']);
  await store.writeFile(id, 'a.amplet', { text: 'again' });
  assert.equal((await store.readFile(id, 'a.amplet')).text, 'again');
});

test('renaming keeps the kind of the content and the extension consistent, in both directions', async () => {
  const { store } = make();
  const kick = { path: 'k.wav', blob: new Blob([new Uint8Array([1, 2])]), mime: 'audio/wav' };
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'a.amplet', text: 'song' }, kick] });
  await assert.rejects(store.renameFile(id, 'a.amplet', 'a.wav'), /holds text/);
  await assert.rejects(store.renameFile(id, 'k.wav', 'k.amplet'), /holds audio/);
  assert.deepEqual((await store.listFiles(id)).map((f) => `${f.path}:${f.kind}`).sort(), ['a.amplet:text', 'k.wav:binary'], 'the originals are intact');
  assert.equal((await store.readFile(id, 'a.amplet')).text, 'song');
  await store.renameFile(id, 'a.amplet', 'b.amplet');
  await store.renameFile(id, 'k.wav', 'k2.wav');
  assert.deepEqual((await store.listFiles(id)).map((f) => `${f.path}:${f.kind}`).sort(), ['b.amplet:text', 'k2.wav:binary']);
});

test('a rename to the name of another file with other capitals is refused; a rename of the file itself to other capitals is not (bug 14)', async () => {
  const { store } = make();
  const { id } = await store.createProject({ name: 'p', files: [{ path: 'components/drums.amplec', text: 'a' }, { path: 'components/new.amplec', text: 'b' }] });
  await assert.rejects(() => store.renameFile(id, 'components/new.amplec', 'components/Drums.amplec'), (error) => error instanceof LiteStoreError && error.code === 'exists');
  assert.equal((await store.listFiles(id)).map((f) => f.path).join(), 'components/drums.amplec,components/new.amplec');
  await store.renameFile(id, 'components/drums.amplec', 'components/Drums.amplec');
  assert.equal((await store.listFiles(id)).map((f) => f.path).join(), 'components/Drums.amplec,components/new.amplec');
});
