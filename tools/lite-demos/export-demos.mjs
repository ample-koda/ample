// Puts a copy of every Lite demo into a folder tree that can be dropped into a normal AMPLE project (Studio): <out>/lite-demos/<id>/ with the files of the demo and the
// sounds it uses next to them. A Studio project resolves imports and sample folders from the project root, so the paths inside the copies get the demo folder in front
// ("components/x.amplec" -> "lite-demos/<id>/components/x.amplec", "samples/kit" -> "lite-demos/<id>/samples/kit"); the demos of Lite itself are not changed.
// Usage: node tools/lite-demos/export-demos.mjs <out folder> [folder name, default lite-demos]

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const lite = path.join(root, 'web/remix');
const out = process.argv[2];
const name = process.argv[3] ?? 'lite-demos';
if (!out) { console.error('Usage: node tools/lite-demos/export-demos.mjs <out folder> [folder name]'); process.exit(2); }

const catalog = JSON.parse(fs.readFileSync(path.join(lite, 'demos/catalog.json'), 'utf8'));
const base = path.join(out, name);
fs.rmSync(base, { recursive: true, force: true });
const isText = (file) => /\.(amplet|amplec|txt|md|json)$/i.test(file);
const prefixed = (text, id) => text
  .replace(/(["\s(])components\//g, `$1${name}/${id}/components/`)
  .replace(/(["\s(])samples\//g, `$1${name}/${id}/samples/`);

let files = 0, bytes = 0;
const put = (target, data) => { fs.mkdirSync(path.dirname(target), { recursive: true }); fs.writeFileSync(target, data); files++; bytes += data.length; };

for (const demo of catalog) {
  const dir = path.join(base, demo.id);
  for (const file of demo.files) {
    const data = fs.readFileSync(path.join(lite, 'demos', demo.id, file));
    put(path.join(dir, file), isText(file) ? Buffer.from(prefixed(data.toString('utf8'), demo.id), 'utf8') : data);
  }
  for (const asset of demo.assets ?? []) {
    for (const file of asset.files) put(path.join(dir, asset.to, file), fs.readFileSync(path.join(lite, 'assets', asset.pack, file)));
  }
}

const index = ['# Lite demos', '', `A copy of the ${catalog.length} demos of AMPLE Remix, one folder each (made by tools/lite-demos/export-demos.mjs). Open the song named below and build it.`, '',
  ...catalog.map((d) => `- ${d.id}/${d.entry ?? d.files.find((f) => f.endsWith('.amplet'))}: ${d.name} (${d.category}, ${d.level})`), ''].join('\n');
put(path.join(base, 'README.md'), Buffer.from(index, 'utf8'));
console.log(`${catalog.length} demos, ${files} files, ${(bytes / 1024 / 1024).toFixed(1)} MB in ${base}`);
