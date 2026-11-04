// Copies the sounds that the Lite demos share from the free (CC0 / public domain) packs of audio-packs/ into web/remix/assets, a few files of each (the demos name which), with the
// LICENSE.txt and SOURCE.md of the pack next to them, and writes assets/index.json (pack, source, licence, files, sizes). Nothing is copied from a pack that is not CC0 or public domain.
// Usage: node tools/lite-demos/make-assets.mjs   (needs the packs fetched: node tools/fetch-audio-packs.mjs big-rusty-drums meatbass emilyguitar)

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const free = path.join(root, 'audio-packs/free');
const target = path.join(root, 'web/remix/assets');

const PACKS = [
  { id: 'drums-rusty', from: 'big-rusty-drums/kit', pack: 'big-rusty-drums', files: ['kick.wav', 'snare.wav', 'hat.wav', 'open.wav', 'tom.wav'], what: 'a drum kit: kick, snare, hat, open hat, tom' },
  { id: 'bass-meat', from: 'meatbass/pizzicato', pack: 'meatbass', files: ['a1.wav', 'c2.wav', 'eb1.wav', 'gb1.wav', 'a2.wav', 'c3.wav', 'gb2.wav'], what: 'a pizzicato bass, one recording per note' },
  { id: 'guitar-emily', from: 'emilyguitar/emily', pack: 'emilyguitar', files: ['a2.wav', 'c3.wav', 'e2.wav', 'eb3.wav', 'gb2.wav', 'a3.wav', 'c4.wav', 'gb3.wav', 'a4.wav'], what: 'a guitar, one recording per note' },
];

const index = [];
for (const p of PACKS) {
  const source = fs.readFileSync(path.join(free, p.pack, 'SOURCE.md'), 'utf8');
  const licence = /Licen[cs]e:\s*(.+)/i.exec(source)?.[1]?.trim() ?? '';
  if (!/CC0|public domain/i.test(licence)) throw new Error(`${p.pack}: licence "${licence}" is not CC0 or public domain; it does not go into Lite.`);
  const url = /Source:\s*(\S+)/i.exec(source)?.[1] ?? '';
  const dir = path.join(target, p.id);
  fs.rmSync(dir, { recursive: true, force: true });
  fs.mkdirSync(dir, { recursive: true });
  const files = [];
  for (const file of p.files) {
    fs.copyFileSync(path.join(free, p.from, file), path.join(dir, file));
    files.push({ file, bytes: fs.statSync(path.join(dir, file)).size });
  }
  fs.copyFileSync(path.join(free, p.pack, 'LICENSE.txt'), path.join(dir, 'LICENSE.txt'));
  fs.writeFileSync(path.join(dir, 'SOURCE.md'), `${source.trimEnd()}\n\n## In AMPLE Remix\n\nPack \`${p.id}\`: ${p.what}. Only these files of the pack are kept here, for the demos: ${p.files.join(', ')}.\nThe licence text is in LICENSE.txt. Made by tools/lite-demos/make-assets.mjs from audio-packs/free/${p.pack}.\n`);
  index.push({ pack: p.id, what: p.what, source: url, licence, files });
}

// sounds that AMPLE made itself (no third-party material): their provenance is in their own folder, written by the script that made them (make-sounds.mjs)
for (const own of fs.existsSync(target) ? fs.readdirSync(target).filter((d) => fs.existsSync(path.join(target, d, 'OWN.json'))) : []) {
  const info = JSON.parse(fs.readFileSync(path.join(target, own, 'OWN.json'), 'utf8'));
  index.push({ pack: own, what: info.what, source: info.source, licence: info.licence, files: fs.readdirSync(path.join(target, own)).filter((f) => /[.](wav|sf2)$/.test(f)).map((file) => ({ file, bytes: fs.statSync(path.join(target, own, file)).size })) });
}

fs.writeFileSync(path.join(target, 'index.json'), JSON.stringify(index, null, 2) + '\n');
console.log(`${index.length} packs, ${index.reduce((n, p) => n + p.files.length, 0)} files, ${(index.reduce((n, p) => n + p.files.reduce((m, f) => m + f.bytes, 0), 0) / 1024 / 1024).toFixed(1)} MB`);
