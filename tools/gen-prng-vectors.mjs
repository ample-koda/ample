// Generates spec-tests/prng/vectors.json from the JavaScript twin. The values are FROZEN for RngVersion 1: regenerating must
// produce identical bytes. Two independent checks guard the generator: published reference outputs (SplitMix64, SipHash-2-4).
// Run: node ample/tools/gen-prng-vectors.mjs [--check]
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { StableRng, splitMix64, sipHash24, deriveSeed, deriveSeed32, RNG_VERSION } from '../web/lib/stable-rng.js';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const target = path.join(root, 'spec-tests', 'prng', 'vectors.json');
const dec = x => x.toString();

// Published reference: SplitMix64 with seed 1234567 (Vigna's reference implementation).
const smReference = ['6457827717110365317', '3203168211198807973', '9817491932198370423', '4593380528125082431', '16408922859458223821'];
const sm = splitMix64(1234567); const smOut = smReference.map(() => dec(sm.next().value));
if (JSON.stringify(smOut) !== JSON.stringify(smReference)) { console.error('SplitMix64 does not match the published reference outputs', smOut); process.exit(1); }
// Published reference: SipHash-2-4 paper, key 00..0f, input 00..0e.
const sipKey0 = 0x0706050403020100n, sipKey1 = 0x0f0e0d0c0b0a0908n;
const sipInput = Uint8Array.from({ length: 15 }, (_, i) => i);
const sipOut = sipHash24(sipKey0, sipKey1, sipInput).toString(16).padStart(16, '0');
if (sipOut !== 'a129ca6149be45e5') { console.error('SipHash-2-4 does not match the published reference output', sipOut); process.exit(1); }

const seeds = ['0', '1', '42', '18446744073709551615'];
const sha = seed => {
  const rng = new StableRng(BigInt(seed)); const buf = Buffer.alloc(8 * 100000);
  for (let i = 0; i < 100000; i++) buf.writeBigUInt64LE(rng.next(), i * 8);
  return crypto.createHash('sha256').update(buf).digest('hex');
};
const first = (seed, n) => { const rng = new StableRng(BigInt(seed)); return Array.from({ length: n }, () => dec(rng.next())); };
const bounds = ['1', '2', '3', '10', '1000', '4294967303', '9223372036854775809'];
const derive = [
  [0n, ['block', 'uuid-1', 'voice']], [0n, ['block', 'uuid-2', 'voice']], [424242n, ['block', 'uuid-1', 'voice']],
  [424242n, ['block', 'uuid-1', 'variant', { n: '3' }]], [18446744073709551615n, ['', 'é😀', { n: '18446744073709551615' }]], [7n, []],
];
const vectors = {
  rngVersion: RNG_VERSION,
  splitmix64: { seed: '1234567', outputs: smOut, note: 'Equal to the published reference outputs for seed 1234567.' },
  siphash24: { k0: dec(sipKey0), k1: dec(sipKey1), input: 'bytes 0..14', expectedHex: sipOut, note: 'Equal to the test vector of the SipHash paper.' },
  xoshiro256ss: seeds.map(seed => ({ seed, first64: first(seed, 64), sha256Of100000: sha(seed) })),
  nextUInt: bounds.map(bound => { const rng = new StableRng(42n); return { seed: '42', bound, first32: Array.from({ length: 32 }, () => dec(rng.nextUInt(BigInt(bound)))) }; }),
  nextUnitBits: (() => { const rng = new StableRng(42n); return { seed: '42', first16: Array.from({ length: 16 }, () => dec(rng.nextUnitBits())) }; })(),
  shuffle: (() => { const rng = new StableRng(42n); return { seed: '42', n: 10, result: rng.shuffle([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]) }; })(),
  derive: derive.map(([master, p]) => ({ master: dec(master), path: p, u64: dec(deriveSeed(master, ...p)), u32: dec(deriveSeed32(master, ...p)) })),
};
const text = JSON.stringify(vectors, null, 2) + '\n';
if (process.argv.includes('--check')) {
  if (!fs.existsSync(target) || fs.readFileSync(target, 'utf8') !== text) { console.error('spec-tests/prng/vectors.json is missing or differs from the generator output (the vectors are frozen).'); process.exit(1); }
  console.log('prng vectors: ok');
} else {
  fs.mkdirSync(path.dirname(target), { recursive: true }); fs.writeFileSync(target, text); console.log('wrote', target);
}
