#!/usr/bin/env node
import { createHash } from 'node:crypto';
import { appendFileSync, closeSync, createReadStream, createWriteStream, existsSync, mkdirSync, openSync, readSync, renameSync, rmSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Readable, Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';

const ROOT = dirname(dirname(fileURLToPath(import.meta.url)));
const INTO_REPOSITORY = process.argv.includes('--into');
const PACKS = process.env.AMPLE_PACK_OUT || (INTO_REPOSITORY ? join(ROOT, 'audio-packs') : join(process.env.AMPLE_HOME || process.env.LOCALAPPDATA || join(process.env.HOME || process.env.USERPROFILE || '.', 'AppData', 'Local'), 'ample', 'audio-packs'));
const OUT = join(PACKS, 'licensed', 'musescore-general');
const BASE = 'https://ftp.osuosl.org/pub/musescore/soundfont/MuseScore_General/';
const ASSETS = ['MuseScore_General.sf2', 'MuseScore_General_License.md', 'MuseScore_General_Sample_Sources.csv'];
const MAX_SF2_BYTES = 512 * 1024 * 1024;
const EXPECTED_SHA256 = 'ee51d2c4b1525e70f19a45909c4fd7a2e26d91d115fa89dbf5a6bc413d8b9bf3';
const METRICS = join(OUT, 'fetch-gm-soundfont-metrics.tsv');

async function measured(step, action) {
  const started = new Date();
  const clock = performance.now();
  let status = 'ok';
  try { return await action(); }
  catch (error) { status = 'failed'; throw error; }
  finally {
    const elapsedMs = Math.max(0, Math.round(performance.now() - clock));
    appendFileSync(METRICS, `${started.toISOString()}\t${step}\t${status}\t${elapsedMs}\n`, 'utf8');
  }
}

async function download(name, expected) {
  const target = join(OUT, name);
  const partial = `${target}.part`;
  if (existsSync(target)) {
    if (!name.endsWith('.sf2')) return { name, status: 'kept-existing', bytes: statSync(target).size, hash: '' };
    const hash = await hashFile(target);
    if (expected && hash === expected) return { name, status: 'verified-existing', bytes: statSync(target).size, hash };
    throw new Error(`${name} exists but does not match the pinned digest; leaving it untouched`);
  }
  const response = await fetch(BASE + name, { redirect: 'error' });
  if (!response.ok || !response.body) throw new Error(`download failed (${response.status}) for ${name}`);
  const declared = Number(response.headers.get('content-length') ?? 0);
  const cap = name.endsWith('.sf2') ? MAX_SF2_BYTES : 16 * 1024 * 1024;
  if (declared > cap) throw new Error(`${name} exceeds its size bound`);
  const hash = createHash('sha256');
  let bytes = 0;
  mkdirSync(dirname(target), { recursive: true });
  try {
    const meter = new Transform({ transform(chunk, _encoding, callback) {
      bytes += chunk.length;
      if (bytes > cap) return callback(new Error(`${name} exceeds its size bound`));
      hash.update(chunk);
      callback(null, chunk);
    } });
    await pipeline(Readable.fromWeb(response.body), meter, createWriteStream(partial, { flags: 'wx' }));
    const digest = hash.digest('hex');
    if (expected && digest !== expected) throw new Error(`${name} digest does not match the pinned value`);
    if (name.endsWith('.sf2')) {
      const handle = openSync(partial, 'r');
      const head = Buffer.alloc(12);
      try { readSync(handle, head, 0, head.length, 0); } finally { closeSync(handle); }
      if (head.toString('ascii', 0, 4) !== 'RIFF' || head.toString('ascii', 8, 12) !== 'sfbk') throw new Error('downloaded asset is not an SF2 bank');
    }
    renameSync(partial, target);
    return { name, status: 'downloaded', bytes, hash: digest };
  } catch (error) {
    if (existsSync(partial)) rmSync(partial);
    throw error;
  }
}

async function hashFile(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}

async function verify() {
  if (EXPECTED_SHA256 === 'PENDING_DOWNLOAD_HASH') throw new Error('SoundFont digest has not yet been pinned in this tool');
  for (const name of ASSETS) await measured(`verify-${name.endsWith('.sf2') ? 'soundfont' : 'provenance'}`, async () => {
    if (!existsSync(join(OUT, name))) throw new Error('required asset is missing');
  });
  const file = join(OUT, ASSETS[0]);
  const size = statSync(file).size;
  const handle = openSync(file, 'r');
  const head = Buffer.alloc(12);
  try { readSync(handle, head, 0, head.length, 0); } finally { closeSync(handle); }
  const digest = await hashFile(file);
  if (size > MAX_SF2_BYTES || head.toString('ascii', 0, 4) !== 'RIFF' || head.toString('ascii', 8, 12) !== 'sfbk' || digest !== EXPECTED_SHA256)
    throw new Error('SoundFont size, signature or pinned digest check failed');
  process.stdout.write(`Verified MuseScore General bank: ${size} bytes, 1 SoundFont, 2 provenance files.\n`);
}

const mode = process.argv[2];
if (!['--download', '--verify'].includes(mode)) {
  process.stderr.write('Usage: node tools/fetch-gm-soundfont.mjs --download|--verify [--into]\n');
  process.exitCode = 2;
} else {
  try {
    mkdirSync(OUT, { recursive: true });
  if (mode === '--verify') await measured('verify-bank', verify);
  else {
    const results = [];
    for (const name of ASSETS) results.push(await measured(`download-${name.endsWith('.sf2') ? 'soundfont' : 'provenance'}`, () => download(name, name.endsWith('.sf2') ? EXPECTED_SHA256 : null)));
    const sf2 = results[0];
    process.stdout.write(`Installed MuseScore General bank: ${sf2.bytes} bytes, 1 SoundFont, 2 provenance files.\n`);
    }
  } catch (error) {
    process.stderr.write(`SoundFont ${mode === '--verify' ? 'verification' : 'download'} failed: ${error.message}\n`);
    process.exitCode = 1;
  }
}
