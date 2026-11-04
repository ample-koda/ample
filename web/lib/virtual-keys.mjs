// The maths of the touch keyboard (virtual-keyboard.js): where the keys are, what a touch height means, and how a recorded note is cut at bar lines. No browser objects. @ample:virtual-keyboard

import { barBeats } from './meter.mjs';
const BLACK = new Set([1, 3, 6, 8, 10]);
const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

export const isBlack = (pitch) => BLACK.has(((pitch % 12) + 12) % 12);
export const pitchName = (pitch) => `${NAMES[((pitch % 12) + 12) % 12]}${Math.floor(pitch / 12) - 1}`;

/** The keys from `first` to `last` (MIDI pitches, both included): { pitch, black, white } where white is the index of the white key (a black key counts the white key before it). */
export function keyLayout(first, last) {
  const keys = [];
  let white = -1;
  for (let pitch = first; pitch <= last; pitch++) {
    const black = isBlack(pitch);
    if (!black) white++;
    keys.push({ pitch, black, white: Math.max(0, white) });
  }
  return { keys, whites: keys.filter((key) => !key.black).length };
}

/** The first pitch of a keyboard of `octaves` octaves that starts at a C at or above `octave` (C4 is 60). The range stays inside 0 to 127. */
export function rangeFor(octave, octaves) {
  const first = Math.max(0, Math.min(127 - octaves * 12, (octave + 1) * 12));
  return { first, last: Math.min(127, first + octaves * 12) };
}

/** A touch at `fraction` of the key height (0 at the top, 1 at the bottom) is a velocity from 30 to 127; 'fixed' is always 100. */
export function velocityAt(fraction, mode = 'touch') {
  if (mode === 'fixed') return 100;
  return Math.round(30 + 97 * Math.min(1, Math.max(0, fraction)));
}

/** Rounds a time in ticks to the grid (0 or less: no snapping). */
export const snapTicks = (ticks, grid) => (grid > 0 ? Math.round(ticks / grid) * grid : Math.round(ticks));

/**
 * A played note as notes of the clip: starts at `at` ticks, lasts `length` ticks, and is cut at every bar line (a note cannot cross one in the notes text; the pieces are tied by sounding one after the other).
 * Every piece is at least one tick long.
 */
export function splitAtBars(at, length, pitch, velocity, ppq, extra = {}) {
  const bar = barBeats() * ppq, pieces = [];
  let start = Math.max(0, Math.round(at)), left = Math.max(1, Math.round(length));
  while (left > 0 && pieces.length < 64) {
    const room = bar - (start % bar), take = Math.min(left, room);
    pieces.push({ at: start, length: take, pitch, velocity, ...extra });
    start += take; left -= take;
  }
  return pieces;
}

// ---- the review of a long take (jam-take-review.js): how many bars it covers, and a bar range of it as notes of its own

/** How many whole bars a take covers (at least one). */
export function takeBars(notes, ppq) {
  const bar = barBeats() * ppq, end = notes.reduce((last, n) => Math.max(last, n.at + n.length), 0);
  return Math.max(1, Math.ceil(end / bar));
}

/**
 * The part of a take from bar `fromBar` (0 based) up to but not including `toBar`, as notes that start at the beginning: a note that starts before the range is left out, one that runs past its end is shortened.
 * Returns { rows, bars }.
 */
export function selectRows(notes, fromBar, toBar, ppq) {
  const bar = barBeats() * ppq, from = Math.max(0, fromBar) * bar, to = Math.max(fromBar + 1, toBar) * bar, rows = [];
  for (const n of notes) {
    if (n.at < from || n.at >= to) continue;
    rows.push({ ...n, at: n.at - from, length: Math.max(1, Math.min(n.length, to - n.at)) });
  }
  return { rows, bars: Math.max(1, Math.round((to - from) / bar)) };
}

/** The bar (0 based) under a pointer at `x` of `width` pixels when the take shows `bars` bars; the end of the strip is the last bar. */
export const barAt = (x, width, bars) => Math.max(0, Math.min(bars - 1, Math.floor((x / Math.max(1, width)) * bars)));

// ---- scales (toonladder-lock), pitch bend and the notes of an ad-hoc take

/** The scales the keyboard can lock to: the semitones above the root. */
export const SCALES = Object.freeze({
  major: [0, 2, 4, 5, 7, 9, 11], minor: [0, 2, 3, 5, 7, 8, 10], 'harmonic minor': [0, 2, 3, 5, 7, 8, 11], dorian: [0, 2, 3, 5, 7, 9, 10], mixolydian: [0, 2, 4, 5, 7, 9, 10],
  pentatonic: [0, 2, 4, 7, 9], 'minor pentatonic': [0, 3, 5, 7, 10], blues: [0, 3, 5, 6, 7, 10],
});
export const ROOTS = Object.freeze(['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']);

/** The pitch classes (0 to 11) of a scale on a root; null for 'off' or an unknown scale. */
export function scaleSet(root, scale) {
  const steps = SCALES[scale];
  return steps ? new Set(steps.map((step) => (((root + step) % 12) + 12) % 12)) : null;
}

/** A pitch moved to the nearest pitch of the scale (a tie goes down); a pitch that is in the scale stays. */
export function snapToScale(pitch, set) {
  if (!set || !set.size || set.has(((pitch % 12) + 12) % 12)) return pitch;
  for (let distance = 1; distance < 12; distance++) {
    if (set.has((((pitch - distance) % 12) + 12) % 12)) return Math.max(0, pitch - distance);
    if (set.has((((pitch + distance) % 12) + 12) % 12)) return Math.min(127, pitch + distance);
  }
  return pitch;
}

/** The value of a MIDI pitch bend message (two 7-bit bytes, 8192 is the middle) as -1 to 1. */
export const bendValue = (lsb, msb) => Math.max(-1, Math.min(1, (((msb & 0x7f) << 7) | (lsb & 0x7f)) / 8192 - 1));

/** Notes of the Jam's note model for an ad-hoc take: seconds to ticks at a tempo, quantised to a grid in beats (0: not). */
export function takeToItems(notes, bpm, ppq, gridBeats) {
  const perSecond = (bpm / 60) * ppq, grid = gridBeats > 0 ? gridBeats * ppq : 0;
  return notes.map((n) => {
    const at = snapTicks(n.at * perSecond, grid), length = grid > 0 ? Math.max(grid, snapTicks((n.end - n.at) * perSecond, grid)) : Math.max(1, Math.round((n.end - n.at) * perSecond));
    return { at, length, pitch: n.pitch, velocity: n.velocity, ...(n.bend ? { bend: n.bend } : {}) };
  });
}

/** A range of keys for the screen: it begins and ends on a white key (a black key at the edge would be cut), at least five white keys (big keys for a finger) and at most six octaves, inside 0 to 127. */
export function snapRange(first, last) {
  let a = Math.max(0, Math.min(127, Math.round(first))), b = Math.max(0, Math.min(127, Math.round(last)));
  if (b < a) [a, b] = [b, a];
  while (isBlack(a) && a > 0) a--;
  while (isBlack(b) && b < 127) b++;
  while (isBlack(b)) b--;
  if (b - a > 72) b = a + 72;
  while (isBlack(b) && b > a) b--;
  while (whiteKeys(a, b) < 5 && b < 127) b++;
  while (whiteKeys(a, b) < 5 && a > 0) a--;
  while (isBlack(b) && b < 127) b++;
  return { first: a, last: b };
}

/** The number of white keys from first to last. */
export const whiteKeys = (first, last) => { let n = 0; for (let p = first; p <= last; p++) if (!isBlack(p)) n++; return n; };

/** A range moved by `delta` semitones with the same number of white keys (so the keys keep their width), beginning on a white key, inside 0 to 127. */
export function moveRange(first, last, delta) {
  const whites = whiteKeys(first, last);
  let a = Math.max(0, Math.min(127, first + delta));
  while (isBlack(a) && a > 0) a--;
  let b = a, seen = isBlack(a) ? 0 : 1;
  while (seen < whites && b < 127) { b++; if (!isBlack(b)) seen++; }
  if (seen < whites) {   // not enough room above: move down instead
    b = Math.min(127, b); while (isBlack(b) && b > a) b--;
    a = b; seen = isBlack(a) ? 0 : 1;
    while (seen < whites && a > 0) { a--; if (!isBlack(a)) seen++; }
  }
  return { first: a, last: b };
}

/** The range with one white key less (or more) at each end: the keys get thicker (or thinner) around the same middle. Never fewer than five white keys, never more than six octaves. */
export function zoomRange(first, last, more) {
  const step = (p, dir) => { let q = p + dir; while (q > 0 && q < 127 && isBlack(q)) q += dir; return Math.max(0, Math.min(127, q)); };
  let a = first, b = last;
  if (more) { a = step(a, -1); b = step(b, 1); } else if (whiteKeys(first, last) > 5) { a = step(a, 1); b = step(b, -1); if (whiteKeys(a, b) < 5) { a = first; b = last; } }
  return snapRange(a, b);
}
