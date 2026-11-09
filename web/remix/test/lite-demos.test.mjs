import assert from 'node:assert/strict';
import { test } from 'node:test';
import { demoFiles, entryOf, filterDemos, loadCatalog } from '../lite-demos.mjs';

const catalog = [
  { id: 'hello', name: 'Hello melody', description: 'A tune', category: 'Basics', level: 'Beginner', tags: ['synth'], files: ['song.amplet'] },
  { id: 'kit', name: 'Drums', description: 'A beat', category: 'Basics', level: 'Beginner', tags: ['drums'], files: ['song.amplet', 'components/x.amplec'], assets: [{ pack: 'drums', to: 'samples/kit', files: ['kick.wav'] }] },
  { id: 'llama', name: 'Delay Llama', description: 'A llama', category: 'Instruments', level: 'Intermediate', tags: ['llama'], files: ['a.amplet'] },
];

const fakeFetch = (files) => async (url) => {
  if (url === '/remix/demos/catalog.json') return new Response(JSON.stringify(catalog));
  return files[url] === undefined ? new Response('', { status: 404 }) : new Response(files[url]);
};

test('the catalog fills in what a demo leaves out', async () => {
  const demos = await loadCatalog(fakeFetch({}));
  assert.equal(demos.length, 3);
  assert.deepEqual(demos[0].assets, []);
});

test('a demo is its own files plus the assets it names, placed in the folder it says', async () => {
  const demo = (await loadCatalog(fakeFetch({}))).find((d) => d.id === 'kit');
  const files = await demoFiles(demo, fakeFetch({ '/remix/demos/kit/song.amplet': 'song', '/remix/demos/kit/components/x.amplec': 'comp', '/remix/assets/drums/kick.wav': new Uint8Array([1, 2]) }));
  assert.deepEqual(files.map((f) => f.path), ['song.amplet', 'components/x.amplec', 'samples/kit/kick.wav']);
  assert.equal(files[0].text, 'song');
  assert.equal(files[2].blob.size, 2);
});

test('a missing file of a demo is said by name', async () => {
  const demo = (await loadCatalog(fakeFetch({}))).find((d) => d.id === 'hello');
  await assert.rejects(demoFiles(demo, fakeFetch({})), /Hello melody.*song\.amplet/);
});

test('a demo with many files is asked a few files at a time, and a 429 is asked again', async () => {
  const demo = { id: 'big', name: 'Big demo', files: Array.from({ length: 40 }, (_, i) => `f${i}.amplet`), assets: [] };
  let running = 0, peak = 0;
  const seen = {};
  const crowded = async (url) => {
    running++; peak = Math.max(peak, running);
    await new Promise((resolve) => setTimeout(resolve, 2));
    running--;
    seen[url] = (seen[url] || 0) + 1;
    if (url.endsWith('f7.amplet') && seen[url] < 3) return new Response('slow down', { status: 429 });   // the server's limit: answered twice, then fine
    return new Response('x');
  };
  const files = await demoFiles(demo, crowded, { concurrency: 8, wait: async () => {} });
  assert.equal(files.length, 40);
  assert.deepEqual(files.map((f) => f.path), demo.files);   // in the order of the demo
  assert.ok(peak <= 8, `at most 8 at once, saw ${peak}`);
  assert.equal(seen['/remix/demos/big/f7.amplet'], 3);
});

test('after a 429 the next try goes around the browser cache (a cached 429 would answer again)', async () => {
  const demo = { id: 'cached', name: 'Cached demo', files: [], assets: [{ pack: 'bass', to: 'samples/bass', files: ['a2.wav'] }] };
  const modes = [];
  const fetchFn = async (url, options) => { modes.push(options?.cache ?? 'default'); return modes.length === 1 ? new Response('', { status: 429 }) : new Response(new Uint8Array([1])); };
  const files = await demoFiles(demo, fetchFn, { wait: async () => {} });
  assert.equal(files[0].path, 'samples/bass/a2.wav');
  assert.deepEqual(modes, ['default', 'reload']);
});

test('a file that stays at 429 is said by name in the end', async () => {
  const demo = { id: 'busy', name: 'Busy demo', files: ['a.amplet'], assets: [] };
  await assert.rejects(demoFiles(demo, async () => new Response('', { status: 429 }), { wait: async () => {} }), /Busy demo.*a\.amplet/);
});

test('search looks in the name, description, tags and level; a category narrows', () => {
  assert.deepEqual(filterDemos(catalog.map((d) => ({ assets: [], ...d }))).length, 3);
  assert.deepEqual(filterDemos(catalog, { query: 'llama' }).map((d) => d.id), ['llama']);
  assert.deepEqual(filterDemos(catalog, { query: 'beginner drums' }).map((d) => d.id), ['kit']);
  assert.deepEqual(filterDemos(catalog, { category: 'Instruments' }).map((d) => d.id), ['llama']);
  assert.deepEqual(filterDemos(catalog, { query: 'nothing here' }), []);
});

test('the song of a demo is the first .amplet unless one is named', () => {
  assert.equal(entryOf({ files: ['components/a.amplec', 'b.amplet', 'c.amplet'] }), 'b.amplet');
  assert.equal(entryOf({ files: ['b.amplet', 'c.amplet'], entry: 'c.amplet' }), 'c.amplet');
});

import { isUnchangedDemo, sharedDemoId, shareUrl } from '../lite-demos.mjs';

test('a link asks for one demo by a plain id and nothing else', () => {
  assert.equal(sharedDemoId('#demo=mozart'), 'mozart');
  assert.equal(sharedDemoId('#demo=house'), 'house');
  assert.equal(sharedDemoId('#demo=../x'), null);
  assert.equal(sharedDemoId('#demo=a&b'), null);
  assert.equal(sharedDemoId('#other=mozart'), null);
  assert.equal(sharedDemoId(''), null);
  assert.equal(shareUrl({ origin: 'https://remix.example.test', pathname: '/' }, { id: 'mozart' }), 'https://remix.example.test/#demo=mozart');
});

test('only a copy that still equals its demo may be shared', async () => {
  const fetched = [{ path: 'song.amplet', text: 'a' }, { path: 'samples/kit/kick.wav', blob: new Blob(['xy']) }];
  const kept = [{ path: 'song.amplet', kind: 'text', size: 1 }, { path: 'samples/kit/kick.wav', kind: 'binary', size: 2 }];
  const read = (texts) => async (path) => ({ kind: 'text', text: texts[path] });
  assert.equal(await isUnchangedDemo(kept, read({ 'song.amplet': 'a' }), fetched), true);
  assert.equal(await isUnchangedDemo(kept, read({ 'song.amplet': 'a2' }), fetched), false, 'a changed text');
  assert.equal(await isUnchangedDemo(kept.slice(0, 1), read({ 'song.amplet': 'a' }), fetched), false, 'a deleted file');
  assert.equal(await isUnchangedDemo([...kept, { path: 'extra.txt', kind: 'text', size: 1 }], read({ 'song.amplet': 'a' }), fetched), false, 'an added file');
  assert.equal(await isUnchangedDemo([kept[0], { ...kept[1], size: 3 }], read({ 'song.amplet': 'a' }), fetched), false, 'a changed sound');
});
