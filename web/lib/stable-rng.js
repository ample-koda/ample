// JavaScript twin of Ample.Audio.Random (RngVersion 1): SplitMix64, xoshiro256**, SipHash-2-4 and seed derivation, on BigInt.
// Must give exactly the numbers of the C# code; both are tested against spec-tests/prng/vectors.json.
const MASK = (1n << 64n) - 1n;
const rotl = (x, k) => ((x << BigInt(k)) | (x >> BigInt(64 - k))) & MASK;
const mul = (a, b) => (a * b) & MASK;
const add = (a, b) => (a + b) & MASK;

export const RNG_VERSION = 1;
export const DOMAIN_KEY = 0x414d504c45534545n;

export function* splitMix64(seed) {
  let state = BigInt.asUintN(64, BigInt(seed));
  for (;;) {
    state = add(state, 0x9e3779b97f4a7c15n);
    let z = state;
    z = mul(z ^ (z >> 30n), 0xbf58476d1ce4e5b9n);
    z = mul(z ^ (z >> 27n), 0x94d049bb133111ebn);
    yield z ^ (z >> 31n);
  }
}

export class StableRng {
  constructor(seed) {
    const g = splitMix64(seed);
    this.s = [g.next().value, g.next().value, g.next().value, g.next().value];
  }

  next() {
    const s = this.s;
    const result = mul(rotl(mul(s[1], 5n), 7), 9n);
    const t = (s[1] << 17n) & MASK;
    s[2] ^= s[0];
    s[3] ^= s[1];
    s[1] ^= s[2];
    s[0] ^= s[3];
    s[2] ^= t;
    s[3] = rotl(s[3], 45);
    return result;
  }

  nextUInt(bound) {
    bound = BigInt(bound);
    if (bound <= 0n) throw new RangeError('bound must be greater than zero');
    const threshold = ((1n << 64n) - bound) % bound;
    for (;;) {
      const r = this.next();
      if (r >= threshold) return r % bound;
    }
  }

  nextUnitBits() { return this.next() >> 11n; }
  nextUnit() { return Number(this.nextUnitBits()) / 9007199254740992; }

  shuffle(items) {
    for (let i = items.length - 1; i > 0; i--) {
      const j = Number(this.nextUInt(BigInt(i) + 1n));
      [items[i], items[j]] = [items[j], items[i]];
    }
    return items;
  }

  pick(items) { return items[Number(this.nextUInt(BigInt(items.length)))]; }
}

function sipRound(v) {
  v[0] = add(v[0], v[1]); v[1] = rotl(v[1], 13); v[1] ^= v[0]; v[0] = rotl(v[0], 32);
  v[2] = add(v[2], v[3]); v[3] = rotl(v[3], 16); v[3] ^= v[2];
  v[0] = add(v[0], v[3]); v[3] = rotl(v[3], 21); v[3] ^= v[0];
  v[2] = add(v[2], v[1]); v[1] = rotl(v[1], 17); v[1] ^= v[2]; v[2] = rotl(v[2], 32);
}

/** SipHash-2-4 of a Uint8Array with the 128-bit key (k0, k1), as a BigInt. */
export function sipHash24(k0, k1, data) {
  k0 = BigInt.asUintN(64, BigInt(k0)); k1 = BigInt.asUintN(64, BigInt(k1));
  const v = [k0 ^ 0x736f6d6570736575n, k1 ^ 0x646f72616e646f6dn, k0 ^ 0x6c7967656e657261n, k1 ^ 0x7465646279746573n];
  const len = data.length, end = len - (len % 8);
  for (let i = 0; i < end; i += 8) {
    let m = 0n;
    for (let j = 7; j >= 0; j--) m = (m << 8n) | BigInt(data[i + j]);
    v[3] ^= m; sipRound(v); sipRound(v); v[0] ^= m;
  }
  let b = BigInt(len) << 56n;
  for (let j = 0; j < len % 8; j++) b |= BigInt(data[end + j]) << BigInt(8 * j);
  v[3] ^= b; sipRound(v); sipRound(v); v[0] ^= b;
  v[2] ^= 0xffn;
  for (let r = 0; r < 4; r++) sipRound(v);
  return v[0] ^ v[1] ^ v[2] ^ v[3];
}

/** Derives a seed: parts are strings (UTF-8) or non-negative integers given as BigInt or { n: "123" }. */
export function deriveSeed(master, ...path) {
  const out = [RNG_VERSION];
  for (const part of path) {
    if (typeof part === 'string') {
      const utf8 = new TextEncoder().encode(part);
      out.push(0x01, utf8.length & 255, (utf8.length >> 8) & 255, (utf8.length >> 16) & 255, (utf8.length >>> 24) & 255, ...utf8);
    } else {
      const n = typeof part === 'bigint' ? part : BigInt(part.n);
      if (n < 0n) throw new RangeError('seed path integers must not be negative');
      out.push(0x02);
      for (let j = 0n; j < 8n; j++) out.push(Number((n >> (8n * j)) & 255n));
    }
  }
  return sipHash24(BigInt.asUintN(64, BigInt(master)), DOMAIN_KEY, Uint8Array.from(out));
}

export function deriveSeed32(master, ...path) { return deriveSeed(master, ...path) & 0xffffffffn; }
