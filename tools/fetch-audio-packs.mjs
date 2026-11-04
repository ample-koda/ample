// Fetches the audio packs listed in audio-packs/packs.json into audio-packs/<tier>/<pack>/<set>/ as 16-bit mono WAV files named for AMPLE
// (drums: kick.wav, snare.wav ...; pitched sets: c4.wav, f#3.wav ...; extra layers and round robins: kick_v2_r1.wav), with the licence text,
// a SOURCE file and a set.json next to them. Only the files a set needs are downloaded (a partial git clone), so a pack costs megabytes, not gigabytes.
// Usage: node tools/fetch-audio-packs.mjs [--dry-run] [--no-sizes] [--into] [pack-id ...]   (no ids: every pack of the manifest)
//   --dry-run   prints the number of files and the estimated size per pack and writes nothing (see audio-packs/README.md)
//   --no-sizes  with --dry-run: only count files (reading sizes can make git download the matched files into the cache)
//   --into      install into the repository audio-packs folder (the default is the user-level AMPLE home)
// Environment: AMPLE_PACK_CACHE (clone cache; choose a folder on a drive with enough space), AMPLE_FFMPEG, AMPLE_PACK_BASE (default https://github.com/).
// Needs git and ffmpeg. The audio itself is not committed (audio-packs/.gitignore); the manifest, licences and sources are.

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const defaultPacksFolder = (env) => env.AMPLE_HOME ? path.join(env.AMPLE_HOME, 'audio-packs') : path.join(env.LOCALAPPDATA || path.join(env.HOME || env.USERPROFILE || '.', 'AppData', 'Local'), 'ample', 'audio-packs');

const NOTES = ['c', 'c#', 'd', 'd#', 'e', 'f', 'f#', 'g', 'g#', 'a', 'a#', 'b'];
export const noteName = (midi) => `${NOTES[midi % 12]}${Math.floor(midi / 12) - 1}`;

/** A free-tier licence must say CC0 or public domain; anything else belongs to the licensed tier (attribution) or is unverified. */
export const licenceIsFree = (text) => /\bCC0\b|public domain/i.test(String(text ?? ''));

/** Splits paths into command lines of at most `limit` characters (the Windows limit is about 32k; stay far below it). */
export function batches(paths, limit = 6000) {
  const out = [];
  let cur = [];
  let len = 0;
  for (const p of paths) {
    if (cur.length && len + p.length + 3 > limit) { out.push(cur); cur = []; len = 0; }
    cur.push(p);
    len += p.length + 3;
  }
  if (cur.length) out.push(cur);
  return out;
}

const sortedUnique = (nums) => [...new Set(nums)].sort((a, b) => a - b);

/**
 * Turns the rules of a set into the files to take: [{ file, name, vl, rr }] and the numbers for set.json.
 * Plain rules (no `vlGroup` or `rrGroup`) behave as before: the first match of each target name wins.
 * Layered rules name the regex groups that hold the source layer (`vlGroup`) and round robin (`rrGroup`) numbers and use {vl} and {rr} in `to`:
 * {vl} becomes `_v<L>` and {rr} becomes `_r<R>` (nothing for 1), so layer 1 round robin 1 is the plain name. Source numbers are sorted and re-indexed
 * 1-based in ascending order, so output names do not depend on the source numbering or the order of the file list. `layerMap` (source layer numbers, ascending)
 * picks which source layers are taken; `rrTake` caps the round robins per layer.
 */
export function planSet(tree, set) {
  const sortedTree = [...tree].sort();
  const files = [];
  const taken = (name) => files.some((f) => f.name === name);
  let layersOffered = 1;
  let layersTaken = 1;
  let roundRobinsTaken = 1;
  for (const rule of set.files) {
    const re = new RegExp(`^${rule.from}$`);
    const layered = rule.vlGroup != null || rule.rrGroup != null;
    const hits = [];
    for (const file of sortedTree) {
      const m = re.exec(file);
      if (!m) continue;
      const baseTo = rule.to.replace(/\{vl\}|\{rr\}/g, '');
      let name = baseTo.replace(/\$(\d)/g, (_, i) => (rule.lower === false ? m[+i] : m[+i].toLowerCase()));
      if (rule.midi) name = noteName(Number(name));   // "60" becomes "c4"
      if (rule.index) name = noteName(rule.index.base + rule.index.step * Number(name));   // "003" with base 21 and step 2 becomes key 27
      hits.push({ file, name, sv: rule.vlGroup != null ? Number(m[rule.vlGroup]) : 1, sr: rule.rrGroup != null ? Number(m[rule.rrGroup]) : 1 });
    }
    if (!layered) {
      for (const h of hits) if (!taken(h.name)) files.push({ file: h.file, name: h.name, vl: 1, rr: 1 });
      continue;
    }
    const byName = new Map();
    for (const h of hits) byName.set(h.name, [...(byName.get(h.name) ?? []), h]);
    for (const [name, list] of [...byName].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
      const allLayers = sortedUnique(list.map((h) => h.sv));
      layersOffered = Math.max(layersOffered, allLayers.length);
      const layers = rule.layerMap ? sortedUnique(rule.layerMap).filter((n) => allLayers.includes(n)) : allLayers;
      layers.forEach((srcLayer, li) => {
        const rrs = sortedUnique(list.filter((h) => h.sv === srcLayer).map((h) => h.sr)).slice(0, rule.rrTake ?? Infinity);
        rrs.forEach((srcRr, ri) => {
          const h = list.find((x) => x.sv === srcLayer && x.sr === srcRr);
          const vl = li + 1;
          const rr = ri + 1;
          const target = withSuffix(name, vl, rr, rule.to);
          if (!taken(target)) files.push({ file: h.file, name: target, vl, rr });
          layersTaken = Math.max(layersTaken, vl);
          roundRobinsTaken = Math.max(roundRobinsTaken, rr);
        });
      });
    }
  }
  return { files, meta: { layersOffered: set.offers?.layers ?? layersOffered, layersTaken, roundRobinsTaken } };
}

/** The placeholders stand at the end of `to` (for example "$1{vl}{rr}"); {vl} and {rr} expand in the order they appear after the name. */
function withSuffix(name, vl, rr, template) {
  const tail = template.slice(Math.min(...['{vl}', '{rr}'].map((p) => (template.includes(p) ? template.indexOf(p) : template.length))));
  return name + tail.replace(/\{vl\}/g, vl > 1 ? `_v${vl}` : '').replace(/\{rr\}/g, rr > 1 ? `_r${rr}` : '');
}

function main() {
  const args = process.argv.slice(2);
  const dry = args.includes('--dry-run');
  const noSizes = args.includes('--no-sizes');
  const intoRepository = args.includes('--into');
  const wanted = args.filter((a) => !a.startsWith('--'));
  const packsDir = path.join(root, 'audio-packs');
  const manifest = JSON.parse(fs.readFileSync(process.env.AMPLE_PACKS_JSON || path.join(packsDir, 'packs.json'), 'utf8'));
  const cache = process.env.AMPLE_PACK_CACHE || path.join(os.tmpdir(), 'ample-pack-cache');
  const ffmpeg = process.env.AMPLE_FFMPEG || 'ffmpeg';
  const base = process.env.AMPLE_PACK_BASE || 'https://github.com/';
  const packsOut = process.env.AMPLE_PACK_OUT || (intoRepository ? packsDir : defaultPacksFolder(process.env));
  const phase = (label, action) => {
    console.log(`  ${label}: started`);
    const started = Date.now();
    try {
      const result = action();
      const elapsed = Date.now() - started;
      console.log(`  ${label}: done in ${elapsed} ms`);
      if (!dry) {
        fs.mkdirSync(packsOut, { recursive: true });
        fs.appendFileSync(path.join(packsOut, 'fetch-audio-packs-metrics.tsv'), `${new Date().toISOString()}\t${label}\t${elapsed}\tok\n`);
      }
      return result;
    } catch (error) {
      const elapsed = Date.now() - started;
      console.log(`  ${label}: failed after ${elapsed} ms`);
      if (!dry) {
        fs.mkdirSync(packsOut, { recursive: true });
        fs.appendFileSync(path.join(packsOut, 'fetch-audio-packs-metrics.tsv'), `${new Date().toISOString()}\t${label}\t${elapsed}\tfailed\n`);
      }
      throw error;
    }
  };

  const run = (cmd, a, cwd) => {
    for (let attempt = 1; ; attempt++) {
      try {
        return execFileSync(cmd, a, { cwd, stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 1 << 28 }).toString();
      } catch (e) {
        // A partial clone fetches missing files on demand; that can fail now and then, so try again.
        if (attempt >= 4) throw new Error(`${cmd} operation failed after ${attempt} attempts (exit ${e.status ?? 'unknown'}).`);
        console.log(`  ${cmd} operation failed (attempt ${attempt}/4); retrying.`);
      }
    }
  };
  const clone = (repo, label) => {
    const dir = path.join(cache, repo.replace(/[^a-z0-9.-]/gi, '_'));
    if (!fs.existsSync(dir)) {
      console.log(`${label}: cloning source repository`);
      fs.mkdirSync(cache, { recursive: true });
      run('git', ['clone', '-q', '--filter=blob:none', '--no-checkout', `${base}${repo}${base.startsWith('http') ? '.git' : ''}`, dir]);
      // A partial clone lazily fetches each missing blob on checkout; git's own auto-gc can fire mid-fetch (once enough loose objects pile up)
      // and race with that lazy fetch, failing it with "unable to read sha1 file" for objects that are perfectly fetchable on retry. Disabled here.
      run('git', ['config', 'gc.auto', '0'], dir);
    }
    return dir;
  };
  const convert = (from, to) => {
    fs.mkdirSync(path.dirname(to), { recursive: true });
    run(ffmpeg, ['-y', '-hide_banner', '-loglevel', 'error', '-i', from, '-ac', '1', '-ar', '44100', '-sample_fmt', 's16', to]);
  };
  const sizeOf = (repoDir, paths) => {
    let total = 0;
    for (const b of batches(paths)) {
      for (const line of run('git', ['ls-tree', '-r', '-l', 'HEAD', '--', ...b], repoDir).split('\n').filter(Boolean)) total += Number(line.split(/\s+/)[3]) || 0;
    }
    return total;
  };

  for (const pack of manifest.packs) {
    if (wanted.length && !wanted.includes(pack.id)) continue;
    if (pack.tier === 'free' && !licenceIsFree(pack.license)) {
      console.error(`${pack.id}: refused, tier free needs a CC0 or public domain licence (move it to tier licensed or unverified)`);
      process.exitCode = 1;
      continue;
    }
    const out = path.join(packsOut, pack.tier, pack.id);
    const label = `${pack.tier}/${pack.id}`;
    console.log(`${label}: reading source index`);
    const repoDir = phase(`${label} source index`, () => clone(pack.repo, label));
    const tree = phase(`${label} source listing`, () => run('git', ['ls-tree', '-r', '--name-only', 'HEAD'], repoDir).split('\n').filter(Boolean));
    const plans = [];
    for (const set of pack.sets) {
      const plan = planSet(tree, set);
      if (!plan.files.length) { console.error(`${pack.id}/${set.name}: no files matched`); process.exitCode = 1; continue; }
      console.log(`  ${set.name}: ${plan.files.length} source files planned`);
      plans.push([set, plan]);
    }
    const all = plans.flatMap(([, p]) => p.files.map((f) => f.file));
    const wantSizes = !noSizes && (dry || pack.maxMB != null);
    const bytes = wantSizes ? sizeOf(repoDir, all) : null;
    const mb = bytes == null ? 'size unknown' : `about ${(bytes / 1048576).toFixed(1)} MB of source files`;
    if (pack.maxMB != null && bytes != null && bytes / 1048576 > pack.maxMB) {
      console.error(`${pack.tier}/${pack.id}: refused, ${all.length} files, ${mb} is over maxMB ${pack.maxMB}`);
      process.exitCode = 1;
      continue;
    }
    if (dry) { console.log(`${label}: ${all.length} files, ${mb} (dry run, nothing written)`); continue; }

    let made = 0;
    for (const [set, plan] of plans) {
      console.log(`  ${set.name}: checking out ${plan.files.length} source files`);
      phase(`${label}/${set.name} checkout`, () => { for (const b of batches(plan.files.map((f) => f.file))) run('git', ['checkout', 'HEAD', '--', ...b], repoDir); });
      let setMade = 0;
      phase(`${label}/${set.name} conversion (${plan.files.length} files)`, () => {
        for (const f of plan.files) {
          convert(path.join(repoDir, f.file), path.join(out, set.name, `${f.name}.wav`));
          made++;
          setMade++;
          if (setMade % 25 === 0 || setMade === plan.files.length) console.log(`  ${set.name}: converted ${setMade}/${plan.files.length}`);
        }
      });
      fs.writeFileSync(path.join(out, set.name, 'set.json'), `${JSON.stringify(plan.meta, null, 2)}\n`);
    }
    for (const [name, text] of Object.entries(pack.documents ?? {})) fs.writeFileSync(path.join(out, name), text);
    if (!licenceIsFree(pack.license) && !(pack.documents ?? {})['ATTRIBUTION.md']) {
      fs.writeFileSync(path.join(out, 'ATTRIBUTION.md'), `# Attribution\n\n${pack.title} by ${pack.author}, ${pack.url}\nLicence: ${pack.license}\n`);
    }
    if (pack.licenseFile) {
      run('git', ['checkout', 'HEAD', '--', pack.licenseFile], repoDir);
      fs.copyFileSync(path.join(repoDir, pack.licenseFile), path.join(out, 'LICENSE.txt'));
    }
    fs.writeFileSync(path.join(out, 'SOURCE.md'), `# ${pack.title}\n\n- Source: ${pack.url}\n- Licence: ${pack.license}\n- Author: ${pack.author}\n- ${pack.note}\n- Tier: ${pack.tier}\n- Fetched with tools/fetch-audio-packs.mjs: ${made} files, converted to 16-bit mono 44.1 kHz WAV.\n`);
    console.log(`${label}: complete, ${made} files across ${plans.length} sets`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) main();
