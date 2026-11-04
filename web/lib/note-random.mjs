// The seed model of AMPLE (docs/language/13-randomness.md), JavaScript twin of src/Ample.Language/NoteRandom.cs. A seed is a 64-bit key in a tree of keys derived by hashing (SHA-256), never by
// adding offsets; a draw is addressed by what is drawn and where, never by "the n-th call so far". Everything is stateless and exact: keys are BigInt. @ample:note-pipeline

import { sha256 } from './sha256.mjs';

const encoder = new TextEncoder();
const DOMAIN = 'ample-seed/1';

/** The first 8 bytes (big endian) of the SHA-256 of the domain and the parts, one per line, as a BigInt. */
export function hash64(...parts) {
  const h = sha256(encoder.encode(`${DOMAIN}\n${parts.join('\n')}`));
  let value = 0n;
  for (let i = 0; i < 8; i++) value = (value << 8n) | BigInt(h[i]);
  return value;
}

/** A key as 16 hexadecimal digits. */
export const hex = (key) => key.toString(16).padStart(16, '0');
/** The root of the song, from the master-seed. */
export const rootKey = (master) => hash64('root', String(BigInt(master)));
/** An explicit, pinned seed: it depends only on the number. */
export const explicitKey = (number) => hash64('explicit', String(BigInt(number)));
/** The key of a song-global stable namespace: it follows the master-seed. */
export const stableKey = (master, name) => hash64('stable', hex(rootKey(master)), name);
/** A child key (a path step, not a cursor); whole-number labels are written with a leading #. */
export const forkKey = (key, label) => hash64('fork', hex(key), label);
/** One draw: what is drawn (the purpose) and where (the address) are part of the derivation, nothing else is. */
export const drawBits = (key, purpose, address) => hash64('draw', hex(key), purpose, address);
/** A whole number below n from 64 random bits. */
export const below = (bits, n) => bits % BigInt(n);
/** The top 53 bits as the numerator of a fraction of 2^53. */
export const unitNumerator = (bits) => bits >> 11n;
export const UNIT_DENOMINATOR = 1n << 53n;

// ---------- scales and chords (the same tables as src/Ample.Language/NoteRandom.cs NoteTheory and web/lib/scales.mjs)

export const SCALES = {
  major: [0, 2, 4, 5, 7, 9, 11],
  minor: [0, 2, 3, 5, 7, 8, 10],
  'harmonic-minor': [0, 2, 3, 5, 7, 8, 11],
  'melodic-minor': [0, 2, 3, 5, 7, 9, 11],
  dorian: [0, 2, 3, 5, 7, 9, 10],
  phrygian: [0, 1, 3, 5, 7, 8, 10],
  lydian: [0, 2, 4, 6, 7, 9, 11],
  mixolydian: [0, 2, 4, 5, 7, 9, 10],
  locrian: [0, 1, 3, 5, 6, 8, 10],
  'pentatonic-major': [0, 2, 4, 7, 9],
  'pentatonic-minor': [0, 3, 5, 7, 10],
  blues: [0, 3, 5, 6, 7, 10],
  'whole-tone': [0, 2, 4, 6, 8, 10],
  'diminished-wh': [0, 2, 3, 5, 6, 8, 9, 11],
  'diminished-hw': [0, 1, 3, 4, 6, 7, 9, 10],
  chromatic: [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11],
};

const QUALITIES = [
  ['maj7', [0, 4, 7, 11]], ['m7b5', [0, 3, 6, 10]], ['dim7', [0, 3, 6, 9]], ['sus2', [0, 2, 7]], ['sus4', [0, 5, 7]], ['add9', [0, 4, 7, 14]],
  ['min', [0, 3, 7]], ['maj', [0, 4, 7]], ['dim', [0, 3, 6]], ['aug', [0, 4, 8]], ['m7', [0, 3, 7, 10]], ['m6', [0, 3, 7, 9]],
  ['7', [0, 4, 7, 10]], ['6', [0, 4, 7, 9]], ['9', [0, 4, 7, 10, 14]], ['m', [0, 3, 7]], ['', [0, 4, 7]],
];
const NATURAL = [9, 11, 0, 2, 4, 5, 7];   // A B C D E F G

/** A root name (C, c#, Db, bb) as a pitch class 0-11, or null. */
export function tryRoot(text) {
  const t = String(text).trim();
  if (t.length < 1 || t.length > 2 || t[0].toLowerCase() < 'a' || t[0].toLowerCase() > 'g') return null;
  let pc = NATURAL[t[0].toLowerCase().charCodeAt(0) - 97];
  if (t.length === 2) { if (t[1] === '#') pc++; else if (t[1] === 'b') pc--; else return null; }
  return ((pc % 12) + 12) % 12;
}

/** A chord symbol (Am, F, G7, C#m7, Bbmaj7) as { root, intervals }, or null. */
export function tryChord(symbol) {
  if (!symbol) return null;
  const rootLength = symbol.length > 1 && (symbol[1] === '#' || symbol[1] === 'b') ? 2 : 1;
  const root = tryRoot(symbol.slice(0, rootLength));
  if (root === null) return null;
  const rest = symbol.slice(rootLength);
  const found = QUALITIES.find(([suffix]) => suffix === rest);
  return found ? { root, intervals: found[1] } : null;
}
