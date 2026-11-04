// A JS port of the engine's deterministic RNG (src/Ample.Audio/Random/{SplitMix64,StableRng,SipHash24,SeedDeriver}.cs), bit-for-bit, using
// BigInt for the 64-bit arithmetic. Verified against spec-tests/prng/vectors.json (the same vectors the C# side's own tests use).
// Used by the piano roll (PR12) to preview the engine's humanize drift before building, without needing a build.

const MASK64 = (1n << 64n) - 1n;
const rotl64 = (x, k) => ((x << BigInt(k)) | (x >> BigInt(64n - BigInt(k)))) & MASK64;

export class SplitMix64 {
  constructor(seed) { this.state = BigInt.asUintN(64, seed); }
  next() {
    this.state = (this.state + 0x9E3779B97F4A7C15n) & MASK64;
    let z = this.state;
    z = ((z ^ (z >> 30n)) * 0xBF58476D1CE4E5B9n) & MASK64;
    z = ((z ^ (z >> 27n)) * 0x94D049BB133111EBn) & MASK64;
    return z ^ (z >> 31n);
  }
}

export class StableRng {
  constructor(seed) {
    const expander = new SplitMix64(BigInt.asUintN(64, seed));
    this.s0 = expander.next(); this.s1 = expander.next(); this.s2 = expander.next(); this.s3 = expander.next();
  }
  next() {
    const result = (rotl64((this.s1 * 5n) & MASK64, 7) * 9n) & MASK64;
    const t = (this.s1 << 17n) & MASK64;
    this.s2 ^= this.s0;
    this.s3 ^= this.s1;
    this.s1 ^= this.s2;
    this.s0 ^= this.s3;
    this.s2 ^= t;
    this.s3 = rotl64(this.s3, 45);
    return result;
  }
  nextUInt(bound) {
    bound = BigInt.asUintN(64, bound);
    if (bound === 0n) throw new RangeError('bound must not be zero');
    const threshold = ((0n - bound) & MASK64) % bound;
    for (;;) { const r = this.next(); if (r >= threshold) return r % bound; }
  }
}

function sipRound(v) {
  v.v0 = (v.v0 + v.v1) & MASK64; v.v1 = rotl64(v.v1, 13); v.v1 ^= v.v0; v.v0 = rotl64(v.v0, 32);
  v.v2 = (v.v2 + v.v3) & MASK64; v.v3 = rotl64(v.v3, 16); v.v3 ^= v.v2;
  v.v0 = (v.v0 + v.v3) & MASK64; v.v3 = rotl64(v.v3, 21); v.v3 ^= v.v0;
  v.v2 = (v.v2 + v.v1) & MASK64; v.v1 = rotl64(v.v1, 17); v.v1 ^= v.v2; v.v2 = rotl64(v.v2, 32);
}

/** SipHash-2-4 of `data` (a Uint8Array) keyed by (k0, k1), both BigInt. Returns a BigInt. */
export function sipHash24(k0, k1, data) {
  k0 = BigInt.asUintN(64, k0); k1 = BigInt.asUintN(64, k1);
  const v = { v0: k0 ^ 0x736F6D6570736575n, v1: k1 ^ 0x646F72616E646F6Dn, v2: k0 ^ 0x6C7967656E657261n, v3: k1 ^ 0x7465646279746573n };
  const length = data.length, end = length - (length % 8);
  const readLE64 = (i) => { let m = 0n; for (let j = 7; j >= 0; j--) m = (m << 8n) | BigInt(data[i + j]); return m; };
  for (let i = 0; i < end; i += 8) {
    const m = readLE64(i);
    v.v3 ^= m; sipRound(v); sipRound(v); v.v0 ^= m;
  }
  let b = BigInt(length) << 56n;
  for (let j = 0; j < length % 8; j++) b |= BigInt(data[end + j]) << BigInt(8 * j);
  v.v3 ^= b; sipRound(v); sipRound(v); v.v0 ^= b;
  v.v2 ^= 0xFFn;
  for (let r = 0; r < 4; r++) sipRound(v);
  return (v.v0 ^ v.v1 ^ v.v2 ^ v.v3) & MASK64;
}

const RNG_VERSION = 1;
const DOMAIN_KEY = 0x414D504C45534545n;   // "AMPLESEE"

/** Encodes one seed-derivation path part the way SeedDeriver.cs does: a string (tag 1, LE uint32 length, UTF-8 bytes) or a
 * non-negative integer/BigInt (tag 2, LE uint64). Appends the encoded bytes to `out` (a plain number array). */
function encodePart(out, part) {
  if (typeof part === 'string') {
    const utf8 = new TextEncoder().encode(part);
    out.push(0x01);
    for (let i = 0; i < 4; i++) out.push((utf8.length >>> (8 * i)) & 0xff);
    for (const b of utf8) out.push(b);
  } else {
    const n = BigInt.asUintN(64, BigInt(part));
    if (n < 0n) throw new RangeError('seed path integers must not be negative');
    out.push(0x02);
    for (let i = 0n; i < 8n; i++) out.push(Number((n >> (8n * i)) & 0xffn));
  }
}

/** The 64-bit seed for a path (block id, purpose, ...), bit-for-bit equal to SeedDeriver.Derive(masterSeed, ...path) in the C# engine. */
export function deriveSeed(masterSeed, path) {
  const bytes = [RNG_VERSION];
  for (const part of path) encodePart(bytes, part);
  return sipHash24(BigInt.asUintN(64, masterSeed), DOMAIN_KEY, Uint8Array.from(bytes));
}
