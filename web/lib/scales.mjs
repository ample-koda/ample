// Scale table and pure helpers for the piano roll (PR03). Mirrors spec/scales.json (the documented source of truth for the roll, the
// arpeggiator, the chord tools and the LSP); kept in sync by hand until a generator reads one into the other.

export const ROOTS = ['c', 'c#', 'd', 'd#', 'e', 'f', 'f#', 'g', 'g#', 'a', 'a#', 'b'];
const ROOT_ALIASES = { db: 1, eb: 3, gb: 6, ab: 8, bb: 10 };

/** Scale id -> ascending semitone offsets from the root (pitch classes 0-11, root always 0). */
export const SCALE_DEGREES = {
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

function rootPitchClass(name) {
  const n = String(name).toLowerCase().trim();
  if (n in ROOT_ALIASES) return ROOT_ALIASES[n];
  const i = ROOTS.indexOf(n);
  return i < 0 ? null : i;
}

/** Parses a clip's `scale` parameter text ("c:minor", "f#:dorian", "none", "", undefined) into { root, scaleId } or null (no scale). */
export function parseScale(text) {
  const t = String(text ?? '').replace(/^"|"$/g, '').trim().toLowerCase();
  if (!t || t === 'none') return null;
  const m = /^([a-g](?:#|b)?)\s*:\s*([a-z-]+)$/.exec(t);
  if (!m) return null;
  const root = rootPitchClass(m[1]);
  if (root === null || !(m[2] in SCALE_DEGREES)) return null;
  return { root, scaleId: m[2] };
}

/** The set of pitch classes (0-11) that belong to a scale, given its root and id. Returns null for no scale (nothing dimmed/glowed). */
export function scaleSet(scale) {
  if (!scale) return null;
  const degrees = SCALE_DEGREES[scale.scaleId];
  if (!degrees) return null;
  return new Set(degrees.map((d) => (d + scale.root) % 12));
}

/** Whether a MIDI pitch is the root of the scale (for the glowing root-row border). */
export function isRoot(pitch, scale) { return !!scale && ((pitch % 12) + 12) % 12 === scale.root; }

/** The nearest in-scale pitch to `pitch` (ties broken upward), searching outward a semitone at a time; the pitch itself if no scale is set. */
export function nearestScaleTone(pitch, scale) {
  const set = scaleSet(scale);
  if (!set) return pitch;
  if (set.has(((pitch % 12) + 12) % 12)) return pitch;
  for (let d = 1; d <= 6; d++) {
    if (pitch + d <= 127 && set.has(((pitch + d) % 12 + 12) % 12)) return pitch + d;
    if (pitch - d >= 0 && set.has(((pitch - d) % 12 + 12) % 12)) return pitch - d;
  }
  return pitch;
}

/** Move one scale degree up or down; out-of-scale notes move to the next scale tone in that direction. */
export function shiftScaleDegree(pitch, direction, scale) {
  const set = scaleSet(scale);
  if (!set || !direction) return pitch;
  for (let next = pitch + Math.sign(direction); next >= 0 && next <= 127; next += Math.sign(direction)) {
    if (set.has(next % 12)) return next;
  }
  return pitch;
}

// Krumhansl-Schmuckler key profiles (major and natural minor), used only as a hint: never written without an explicit click.
const MAJOR_PROFILE = [6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88];
const MINOR_PROFILE = [6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17];

function correlate(histogram, profile, rotation) {
  const rotated = profile.map((_, i) => profile[(i - rotation + 12) % 12]);
  const meanH = histogram.reduce((a, b) => a + b, 0) / 12, meanP = rotated.reduce((a, b) => a + b, 0) / 12;
  let num = 0, denH = 0, denP = 0;
  for (let i = 0; i < 12; i++) { const dh = histogram[i] - meanH, dp = rotated[i] - meanP; num += dh * dp; denH += dh * dh; denP += dp * dp; }
  const den = Math.sqrt(denH * denP);
  return den ? num / den : 0;
}

/** A best-guess key for a set of absolute notes (as from notes-model's absolute()), as { root, mode: 'major'|'minor', confidence }, or null for no notes. */
export function detectKey(notes) {
  if (!notes?.length) return null;
  const histogram = new Array(12).fill(0);
  for (const n of notes) histogram[((n.pitch % 12) + 12) % 12] += Math.max(1, n.length);
  let best = null;
  for (let root = 0; root < 12; root++) {
    for (const [mode, profile] of [['major', MAJOR_PROFILE], ['minor', MINOR_PROFILE]]) {
      const score = correlate(histogram, profile, root);
      if (!best || score > best.confidence) best = { root, mode, confidence: score };
    }
  }
  return best;
}
