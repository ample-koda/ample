import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';
import { SplitMix64, StableRng, sipHash24, deriveSeed } from '../stable-rng.mjs';

// The same reference vectors tests/Ample.Core.Tests/PrngTests.cs checks the C# engine against (spec-tests/prng/vectors.json), so this JS
// port (used by the piano roll's humanize preview, PR12) is verified bit-for-bit against the engine, not just internally consistent.
const vectors = JSON.parse(fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../spec-tests/prng/vectors.json'), 'utf8'));

test('SplitMix64 equals the published reference outputs', () => {
  const g = new SplitMix64(BigInt(vectors.splitmix64.seed));
  for (const expected of vectors.splitmix64.outputs) assert.equal(g.next(), BigInt(expected));
});

test('SipHash24 equals the paper vector', () => {
  const input = Uint8Array.from({ length: 15 }, (_, i) => i);
  const h = sipHash24(BigInt(vectors.siphash24.k0), BigInt(vectors.siphash24.k1), input);
  assert.equal(h.toString(16).padStart(16, '0'), vectors.siphash24.expectedHex);
});

test('xoshiro256** outputs equal the vectors for every seed', () => {
  for (const e of vectors.xoshiro256ss) {
    const rng = new StableRng(BigInt(e.seed));
    for (const expected of e.first64) assert.equal(rng.next(), BigInt(expected));
  }
});

test('nextUInt is bounded and equals the vectors for every (seed, bound)', () => {
  for (const e of vectors.nextUInt) {
    const rng = new StableRng(BigInt(e.seed));
    const bound = BigInt(e.bound);
    for (const expected of e.first32) assert.equal(rng.nextUInt(bound), BigInt(expected));
  }
});

test('deriveSeed equals the vectors, including string and BigInt path parts', () => {
  for (const d of vectors.derive) {
    const path = d.path.map((p) => (typeof p === 'object' ? BigInt(p.n) : p));
    assert.equal(deriveSeed(BigInt(d.master), path), BigInt(d.u64));
  }
});

test('deriveSeed depends on every part of the path and the master seed', () => {
  const base = deriveSeed(5n, ['a', 'b']);
  assert.notEqual(deriveSeed(6n, ['a', 'b']), base);
  assert.notEqual(deriveSeed(5n, ['b', 'a']), base);
  assert.notEqual(deriveSeed(5n, ['ab', 'c']), deriveSeed(5n, ['a', 'bc']));
});
