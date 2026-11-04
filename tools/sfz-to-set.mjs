#!/usr/bin/env node
// Converts the common subset of an SFZ instrument into an AMPLE sample set folder (A02):
//   <region> sample, key / lokey / hikey / pitch_keycenter, lovel / hivel, seq_length / seq_position, default_path (also from <group>, <master>, <global>).
// Output: <out>/<note>.wav, <note>_v<layer>.wav, <note>_v<layer>_r<round-robin>.wav (note names like c4, f#3; layers are the distinct velocity ranges of a note, lowest first,
// and AMPLE splits the velocity range evenly over them). Only WAV files are copied. Nothing is converted, resampled or normalised.
// Usage: node tools/sfz-to-set.mjs <instrument.sfz> <out-folder> [--dry]
import fs from 'node:fs';
import path from 'node:path';

const NOTES = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };
const NAMES = ['c', 'c#', 'd', 'd#', 'e', 'f', 'f#', 'g', 'g#', 'a', 'a#', 'b'];

/** A note number from an SFZ value: 60 or a name such as c4, c#4, db4 (C4 is 60). */
export function noteNumber(value) {
  if (value === undefined) return null;
  if (/^\d+$/.test(value)) return Number(value);
  const m = /^([a-gA-G])([#b]?)(-?\d+)$/.exec(value);
  if (!m) return null;
  return (Number(m[3]) + 1) * 12 + NOTES[m[1].toLowerCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0);
}

export const noteName = (n) => `${NAMES[((n % 12) + 12) % 12]}${Math.floor(n / 12) - 1}`;

/** Reads the regions of an SFZ text with the opcodes of their group, master and global headers merged in. */
export function parseSfz(text) {
  const clean = text.replace(/\/\/.*$/gm, '');
  const tokens = clean.split(/(<\w+>)/).map((t) => t.trim()).filter(Boolean);
  const scopes = { global: {}, master: {}, group: {} };
  const regions = [];
  let header = null;
  for (const token of tokens) {
    const h = /^<(\w+)>$/.exec(token);
    if (h) {
      header = h[1];
      if (header === 'global') { scopes.global = {}; scopes.master = {}; scopes.group = {}; }
      if (header === 'master') { scopes.master = {}; scopes.group = {}; }
      if (header === 'group') scopes.group = {};
      if (header === 'region') regions.push({ ...scopes.global, ...scopes.master, ...scopes.group });
      continue;
    }
    // opcodes: name=value, values may contain spaces only in sample paths (up to the next name=)
    const ops = {};
    const re = /([\w$]+)=([^=]*?)(?=\s+[\w$]+=|$)/gs;
    let m;
    while ((m = re.exec(token))) ops[m[1]] = m[2].trim();
    if (header === 'region') Object.assign(regions[regions.length - 1], ops);
    else if (header && scopes[header]) Object.assign(scopes[header], ops);
  }

  return regions;
}

/** Plans the files of a set: [{ from (relative to the sfz), to (file name) }] and the warnings. */
export function planSet(regions) {
  const warnings = [];
  const entries = [];
  for (const r of regions) {
    if (!r.sample) continue;
    if (!/\.wav$/i.test(r.sample)) { warnings.push(`skipped ${r.sample}: only WAV files are copied`); continue; }
    const centre = noteNumber(r.pitch_keycenter) ?? noteNumber(r.key) ?? noteNumber(r.lokey);
    if (centre === null) { warnings.push(`skipped ${r.sample}: no key or pitch_keycenter`); continue; }
    const file = path.posix.join((r.default_path ?? '').replaceAll('\\', '/'), r.sample.replaceAll('\\', '/'));
    entries.push({ from: file, note: centre, lo: Number(r.lovel ?? 0), hi: Number(r.hivel ?? 127), seq: Number(r.seq_position ?? 0) });
  }

  const planned = [];
  const byNote = new Map();
  for (const e of entries) {
    if (!byNote.has(e.note)) byNote.set(e.note, []);
    byNote.get(e.note).push(e);
  }

  for (const [note, list] of byNote) {
    const ranges = [...new Set(list.map((e) => `${e.lo}-${e.hi}`))].sort((a, b) => Number(a.split('-')[0]) - Number(b.split('-')[0]));
    for (const [layerIndex, range] of ranges.entries()) {
      const inLayer = list.filter((e) => `${e.lo}-${e.hi}` === range).sort((a, b) => a.seq - b.seq || a.from.localeCompare(b.from));
      inLayer.forEach((e, rr) => {
        let name = noteName(note);
        if (ranges.length > 1 || inLayer.length > 1) name += `_v${layerIndex + 1}`;
        if (inLayer.length > 1) name += `_r${rr + 1}`;
        planned.push({ from: e.from, to: `${name}.wav` });
      });
    }
  }

  const seen = new Set();
  for (const p of planned) {
    if (seen.has(p.to)) warnings.push(`duplicate name ${p.to}`);
    seen.add(p.to);
  }

  return { files: planned, warnings };
}

if (process.argv[1]?.endsWith('sfz-to-set.mjs')) {
  const [sfzPath, out, flag] = process.argv.slice(2);
  if (!sfzPath || !out) { console.error('usage: node tools/sfz-to-set.mjs <instrument.sfz> <out-folder> [--dry]'); process.exit(2); }
  const { files, warnings } = planSet(parseSfz(fs.readFileSync(sfzPath, 'utf8')));
  const base = path.dirname(sfzPath);
  if (flag !== '--dry') fs.mkdirSync(out, { recursive: true });
  let copied = 0;
  for (const f of files) {
    const source = path.join(base, f.from);
    if (!fs.existsSync(source)) { warnings.push(`missing ${f.from}`); continue; }
    if (flag !== '--dry') fs.copyFileSync(source, path.join(out, f.to));
    copied++;
  }

  console.log(`${copied} of ${files.length} files ${flag === '--dry' ? 'planned' : 'copied'} to ${out}`);
  for (const w of warnings) console.log('warning: ' + w);
}
