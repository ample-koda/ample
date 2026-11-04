import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { batches, defaultPacksFolder, licenceIsFree, planSet } from './fetch-audio-packs.mjs';

const tool = path.join(path.dirname(fileURLToPath(import.meta.url)), 'fetch-audio-packs.mjs');
const names = (set, tree) => planSet(tree, set).files.map((f) => f.name);

test('plain rules behave as before: first match wins, captures lower-cased', () => {
  const set = { files: [{ from: 'a/(K)ick\\.flac', to: '$1' }, { from: 'a/Kick\\.flac', to: 'kick' }, { from: 'a/(K)ick\\.flac', to: '$1' }] };
  assert.deepEqual(names(set, ['a/Kick.flac']), ['k', 'kick']);
});

test('layers and round robins are sorted and re-indexed 1-based, whatever the source numbers or order', () => {
  const tree = ['k_vl6_rr3.flac', 'k_vl4_rr9.flac', 'k_vl4_rr2.flac', 'k_vl6_rr1.flac', 'k_vl1_rr1.flac'];
  const set = { files: [{ from: 'k_vl(\\d+)_rr(\\d+)\\.flac', to: 'kick{vl}{rr}', vlGroup: 1, rrGroup: 2 }] };
  const plan = planSet(tree, set);
  assert.deepEqual(plan.files.map((f) => `${f.name}=${f.file}`).sort(), [
    'kick=k_vl1_rr1.flac', 'kick_v2=k_vl4_rr2.flac',
    'kick_v2_r2=k_vl4_rr9.flac', 'kick_v3=k_vl6_rr1.flac', 'kick_v3_r2=k_vl6_rr3.flac',
  ].sort());
  assert.deepEqual(plan.meta, { layersOffered: 3, layersTaken: 3, roundRobinsTaken: 2 });
  assert.deepEqual(planSet([...tree].reverse(), set).files, plan.files);
});

test('layerMap picks source layers, rrTake caps round robins, offers is kept', () => {
  const tree = ['k_vl1_rr1.flac', 'k_vl2_rr1.flac', 'k_vl2_rr2.flac', 'k_vl3_rr1.flac'];
  const set = { offers: { layers: 6 }, files: [{ from: 'k_vl(\\d+)_rr(\\d+)\\.flac', to: 'kick{vl}{rr}', vlGroup: 1, rrGroup: 2, layerMap: [3, 2], rrTake: 1 }] };
  const plan = planSet(tree, set);
  assert.deepEqual(plan.files.map((f) => f.name), ['kick', 'kick_v2']);
  assert.deepEqual(plan.files.map((f) => f.file), ['k_vl2_rr1.flac', 'k_vl3_rr1.flac']);
  assert.equal(plan.meta.layersOffered, 6);
});

test('licence guard and batching', () => {
  assert.ok(licenceIsFree('CC0 1.0 (public domain dedication)'));
  assert.ok(licenceIsFree('Public Domain'));
  assert.ok(!licenceIsFree('Creative Commons Attribution 4.0'));
  assert.ok(!licenceIsFree(undefined));
  const b = batches(Array.from({ length: 100 }, (_, i) => `path/to/file-${i}.flac`), 200);
  assert.ok(b.length > 5 && b.every((x) => x.join('   ').length <= 200) && b.flat().length === 100);
});

test('the default destination is the user-level AMPLE audio-packs folder', () => {
  const localAppData = ['C:', 'Users', 'Test', 'AppData', 'Local'].join('\\');   // a Windows-style example folder, written in parts
  assert.equal(defaultPacksFolder({ LOCALAPPDATA: localAppData }), path.join(localAppData, 'ample', 'audio-packs'));
  assert.equal(defaultPacksFolder({ AMPLE_HOME: 'custom-home', LOCALAPPDATA: 'C:\\ignored' }), path.join('custom-home', 'audio-packs'));
});

test('every entry of the real packs.json is structurally sound (no network: catches typos before a fetch attempt)', () => {
  const manifestPath = path.join(path.dirname(tool), '..', 'audio-packs', 'packs.json');
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  assert.ok(Array.isArray(manifest.packs) && manifest.packs.length > 0, 'packs.json must list at least one pack');
  const ids = new Set();
  for (const pack of manifest.packs) {
    const where = `pack "${pack.id ?? '(no id)'}"`;
    assert.ok(pack.id && typeof pack.id === 'string', `${where}: id is required`);
    assert.ok(!ids.has(pack.id), `${where}: id is not unique`);
    ids.add(pack.id);
    assert.ok(['free', 'licensed', 'unverified'].includes(pack.tier), `${where}: tier must be free, licensed or unverified`);
    assert.ok(pack.title && pack.repo && pack.url && pack.author && pack.note, `${where}: title, repo, url, author and note are required`);
    assert.ok(pack.license && typeof pack.license === 'string', `${where}: license text is required`);
    if (pack.tier === 'free') assert.ok(licenceIsFree(pack.license), `${where}: tier free but the license text does not say CC0 or public domain - move it to licensed or unverified`);
    assert.ok(pack.licenseFile && typeof pack.licenseFile === 'string', `${where}: licenseFile is required`);
    assert.ok(Array.isArray(pack.sets) && pack.sets.length > 0, `${where}: at least one set is required`);
    const setNames = new Set();
    for (const set of pack.sets) {
      const setWhere = `${where}, set "${set.name ?? '(no name)'}"`;
      assert.ok(set.name && typeof set.name === 'string', `${setWhere}: name is required`);
      assert.ok(!setNames.has(set.name), `${setWhere}: set name is not unique within the pack`);
      setNames.add(set.name);
      assert.ok(Array.isArray(set.files) && set.files.length > 0, `${setWhere}: at least one file rule is required`);
      for (const rule of set.files) {
        const ruleWhere = `${setWhere}, rule "${rule.from}"`;
        assert.ok(rule.from && typeof rule.from === 'string', `${setWhere}: every rule needs "from"`);
        assert.doesNotThrow(() => new RegExp(`^${rule.from}$`), `${ruleWhere}: "from" is not a valid regular expression`);
        assert.ok(rule.to && typeof rule.to === 'string', `${ruleWhere}: "to" is required`);
      }
    }
  }
});

test('dry run against a local git repository: counts, sizes, maxMB refusal, free-tier licence refusal, nothing written', () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-fetch-'));
  const src = path.join(tmp, 'src');
  fs.mkdirSync(src);
  for (const f of ['k_vl1_rr1', 'k_vl2_rr1', 'k_vl2_rr2']) fs.writeFileSync(path.join(src, `${f}.flac`), Buffer.alloc(1048576 / 2));
  const git = (...a) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t' + String.fromCharCode(64) + 'example.test', ...a], { cwd: src, stdio: 'pipe' });
  git('init', '-q');
  git('add', '.');
  git('commit', '-q', '-m', 'x');
  const pack = (over) => ({ id: 'p', tier: 'free', title: 'T', repo: 'src', url: 'u', license: 'CC0 1.0', author: 'a', note: 'n', sets: [{ name: 's', files: [{ from: 'k_vl(\\d+)_rr(\\d+)\\.flac', to: 'kick{vl}{rr}', vlGroup: 1, rrGroup: 2 }] }], ...over });
  const run = (p, ...args) => {
    const json = path.join(tmp, 'packs.json');
    fs.writeFileSync(json, JSON.stringify({ packs: [p] }));
    const env = { ...process.env, AMPLE_PACKS_JSON: json, AMPLE_PACK_BASE: `${tmp}${path.sep}`, AMPLE_PACK_CACHE: path.join(tmp, 'cache'), AMPLE_PACK_OUT: path.join(tmp, 'out') };
    try { return { code: 0, out: execFileSync('node', [tool, '--dry-run', ...args], { env, stdio: 'pipe' }).toString() }; }
    catch (e) { return { code: e.status, out: `${e.stdout}${e.stderr}` }; }
  };
  const ok = run(pack({}));
  assert.equal(ok.code, 0, ok.out);
  assert.match(ok.out, /3 files, about 1\.5 MB/);
  assert.match(ok.out, /s: 3 source files planned/);
  assert.match(ok.out, /dry run, nothing written/);
  const over = run(pack({ maxMB: 1 }));
  assert.equal(over.code, 1);
  assert.match(over.out, /over maxMB 1/);
  const lic = run(pack({ license: 'CC BY 4.0' }));
  assert.equal(lic.code, 1);
  assert.match(lic.out, /refused, tier free/);
  assert.equal(run(pack({ tier: 'licensed', license: 'CC BY 4.0' }), '--no-sizes').out.includes('size unknown'), true);
  assert.equal(fs.existsSync(path.join(tmp, 'out')), false);
  fs.rmSync(tmp, { recursive: true, force: true });
});
