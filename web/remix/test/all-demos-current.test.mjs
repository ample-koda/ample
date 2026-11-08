// "All demos in one project" must contain every other demo, unchanged apart from the folder in front of its paths
// (tools/lite-demos/make-all-demos.mjs makes it: run it after changing or adding any demo).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const demos = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../demos');
const catalog = JSON.parse(fs.readFileSync(path.join(demos, 'catalog.json'), 'utf8'));
const all = catalog.find((d) => d.id === 'all-demos');

test('all-demos is in the catalog, in Advanced, and opens Welcome to AMPLE', () => {
  assert.ok(all, 'run: node tools/lite-demos/make-all-demos.mjs');
  assert.equal(all.category, 'Advanced');
  assert.equal(all.entry, 'song.amplet');
  const welcome = fs.readFileSync(path.join(demos, 'welcome-to-ample/song.amplet'), 'utf8');
  assert.equal(fs.readFileSync(path.join(demos, 'all-demos/song.amplet'), 'utf8'), welcome, 'Welcome changed: run node tools/lite-demos/make-all-demos.mjs');
});

test('every other demo has all its files in all-demos, and its sounds are listed', () => {
  for (const d of catalog) {
    if (d.id === 'all-demos' || d.id === 'welcome-to-ample') continue;
    for (const f of d.files) assert.ok(all.files.includes(`${d.id}/${f}`), `${d.id}/${f} is missing from all-demos: run node tools/lite-demos/make-all-demos.mjs`);
    for (const a of d.assets ?? []) assert.ok(all.assets.some((x) => x.pack === a.pack && x.to === a.to), `${d.id}: the sounds ${a.pack} are missing from all-demos`);
  }
  assert.ok(all.files.length + all.assets.reduce((n, a) => n + a.files.length, 0) <= 200, 'a Remix project has at most 200 files');
});
