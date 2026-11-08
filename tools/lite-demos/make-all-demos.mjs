// Makes the demo "All demos in one project" (web/remix/demos/all-demos + its entry at the end of catalog.json) from the demos that are in catalog.json.
// Welcome to AMPLE is the song that opens (song.amplet at the root); every other demo sits in its own folder <id>/ with its files, and the sounds are shared in samples/.
// Paths inside a demo are made project-relative like in export-demos.mjs (components/x -> <id>/components/x, a sibling file such as mozart.ampleseq gets the folder too).
// RULE: run this after ANY change to a demo or after adding one (node tools/lite-demos/make-all-demos.mjs), commit the result, deploy Remix. A test checks that it is current.
// It is separate from make-demos.mjs on purpose: that script is behind the demos that were edited directly, so running it would undo their changes.

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const demosDir = path.join(root, 'web/remix/demos');
const ID = 'all-demos';
const catalogPath = path.join(demosDir, 'catalog.json');
const catalog = JSON.parse(fs.readFileSync(catalogPath, 'utf8')).filter((d) => d.id !== ID);
const isText = (file) => /\.(amplet|amplec|ampleseq|txt|md|json)$/i.test(file);
const read = (d, file) => fs.readFileSync(path.join(demosDir, d.id, file));

const files = new Map();
const assets = new Map();
const index = [];
const welcome = catalog.find((d) => d.id === 'welcome-to-ample');
files.set('song.amplet', read(welcome, 'song.amplet'));
for (const d of catalog) {
  if (d === welcome) continue;
  const own = d.files.filter((p) => !p.endsWith('.amplet') && !p.startsWith('components/'));
  const fix = (text) => {
    let s = text.replace(/(["\s(])components\//g, `$1${d.id}/components/`);
    for (const p of own) s = s.split(` ${p}`).join(` ${d.id}/${p}`).split(`"${p}`).join(`"${d.id}/${p}`);
    return s;
  };
  for (const p of d.files) { const data = read(d, p); files.set(`${d.id}/${p}`, isText(p) ? Buffer.from(fix(data.toString('utf8')), 'utf8') : data); }
  for (const a of d.assets ?? []) assets.set(`${a.pack}>${a.to}`, a);
  index.push(`- ${d.id}/${d.entry ?? d.files.find((f) => f.endsWith('.amplet'))}: ${d.name} (${d.category}, ${d.level})`);
}
files.set('README.md', Buffer.from(['# All AMPLE demos', '', 'song.amplet (Welcome to AMPLE) is the song that opens. Every other demo is in its own folder: open its song and play it.', '', ...index, ''].join('\n'), 'utf8'));

const total = files.size + [...assets.values()].reduce((n, a) => n + a.files.length, 0);
if (total > 190) throw new Error(`All demos in one project would be ${total} files; a Remix project has at most 200: split it.`);

const out = path.join(demosDir, ID);
fs.rmSync(out, { recursive: true, force: true });
for (const [p, data] of files) { const target = path.join(out, p); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.writeFileSync(target, data); }
const entry = {
  id: ID, name: 'All demos in one project',
  description: 'Every demo of Remix in one project, with Welcome to AMPLE open: one folder for each demo, the sounds shared. Browse the file list, open any song and play it.',
  category: 'Advanced', level: 'Advanced', tags: ['all', 'library', 'demos', 'welcome'],
  files: [...files.keys()], assets: [...assets.values()], entry: 'song.amplet',
};
fs.writeFileSync(catalogPath, JSON.stringify([...catalog, entry], null, 2) + '\n');
console.log(`${ID}: ${catalog.length} demos, ${total} files`);
