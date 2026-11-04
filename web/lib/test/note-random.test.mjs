import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { sha256 } from '../sha256.mjs';
import { SCALES, below, drawBits, explicitKey, forkKey, hex, rootKey, stableKey, tryChord, tryRoot } from '../note-random.mjs';

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../spec-tests/note-pipelines');
const vectors = JSON.parse(fs.readFileSync(path.join(dir, 'seeds.json'), 'utf8'));
const keyOf = (master, steps) => {
  let key = null;
  for (const step of steps) {
    const [kind, ...rest] = step.split(':'), value = rest.join(':');
    key = kind === 'stable' ? stableKey(BigInt(master), value) : kind === 'explicit' ? explicitKey(BigInt(value)) : forkKey(key, value);
  }
  return key;
};

test('sha256 gives the standard digests', () => {
  const h = (text) => Buffer.from(sha256(new TextEncoder().encode(text))).toString('hex');
  assert.equal(h('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  assert.equal(h(''), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855');
  assert.equal(h('abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq'), '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1');
  assert.equal(h('a'.repeat(1000)), '41edece42d63e8d9bf515a9ba6932e1c20cbc9f5a5d134645adb5db1b9737ea3');
});

test('the roots and the keys are the golden ones', () => {
  let n = 0;
  for (const k of vectors.keys) {
    if (k.root) assert.equal(hex(rootKey(BigInt(k.master))), k.root);
    else assert.equal(hex(keyOf(k.master, k.path)), k.key, JSON.stringify(k.path));
    n++;
  }
  assert.ok(n > 30);
});

test('the draws are the golden ones', () => {
  for (const d of vectors.draws) assert.equal(hex(drawBits(keyOf(d.master, d.path), d.purpose, d.address)), d.bits, JSON.stringify(d));
  assert.ok(vectors.draws.length > 200);
});

test('a key depends only on its path; forks differ by label, number and order; the master matters only for stable', () => {
  const verse = stableKey(1n, 'verse');
  assert.equal(verse, stableKey(1n, 'verse'));
  const labels = ['chords', 'timing', 'fills', '#3', '3'].map((l) => forkKey(verse, l));
  assert.equal(new Set(labels).size, labels.length);
  assert.notEqual(forkKey(forkKey(verse, 'a'), 'b'), forkKey(forkKey(verse, 'b'), 'a'));
  assert.notEqual(stableKey(1n, 'verse'), stableKey(2n, 'verse'));
  assert.equal(explicitKey(7n), explicitKey(7n));
});

test('draws are spread evenly enough for a song', () => {
  const key = stableKey(7n, 'spread'), buckets = new Array(10).fill(0);
  for (let i = 0; i < 5000; i++) buckets[Number(below(drawBits(key, 'random', String(i)), 10))]++;
  for (const b of buckets) assert.ok(b >= 400 && b <= 600, String(b));
});

test('scales, roots and chord symbols', () => {
  const spec = JSON.parse(fs.readFileSync(path.join(dir, '../../spec/scales.json'), 'utf8'));
  for (const [id, intervals] of Object.entries(spec.scales)) if (SCALES[id]) assert.deepEqual(SCALES[id], intervals, id);
  assert.equal(tryRoot('C#'), 1);
  assert.equal(tryRoot('bb'), 10);
  assert.equal(tryRoot('H'), null);
  assert.deepEqual(tryChord('Am'), { root: 9, intervals: [0, 3, 7] });
  assert.deepEqual(tryChord('Bbmaj7'), { root: 10, intervals: [0, 4, 7, 11] });
  assert.equal(tryChord('H'), null);
  assert.equal(tryChord('Amaj9x'), null);
});
