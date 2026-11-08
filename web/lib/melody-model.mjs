// The text of a midi-clip `melody` and `chords` property as a model that an editor can change, and back to text. Pure, no DOM. @ample:melody-editor
// melody: note name (a-g, # or b), octave, length in beats, optional velocity (c4:1:60); r:2 is a rest; the words pp p mp mf f ff and < > are dynamics markers that take no time.
// chords: one name per bar (Am F G7 Cmaj7 Dsus4 Bbm7), C/E puts E lowest, Am.F puts two chords in one bar.

import { CHORDS, chordPitches } from './chords.mjs';

const LETTER = { c: 0, d: 2, e: 4, f: 5, g: 7, a: 9, b: 11 };
const SHARP_NAMES = ['c', 'c#', 'd', 'd#', 'e', 'f', 'f#', 'g', 'g#', 'a', 'a#', 'b'];
export const LEVELS = { pp: 32, p: 48, mp: 64, mf: 80, f: 96, ff: 112 };
export const DEFAULT_VELOCITY = 96;
const MARK = /^(pp|p|mp|mf|f|ff|<|>)$/i;
const NOTE = /^([a-gA-G])([#b]?)(-?\d+)(?::(\d*\.?\d+))?(?::(\d+))?$/;
const REST = /^[rR](?::(\d*\.?\d+))?$/;

/** The number as short text: at most six decimals, no trailing zeros. */
export const fmt = (n) => String(Math.round(n * 1e6) / 1e6);

/** MIDI number of a note name like c4, f#3, bb2 (c4 is 60), or null. */
export function midiOf(name) {
  const m = /^([a-gA-G])([#b]?)(-?\d+)$/.exec(String(name).trim());
  if (!m) return null;
  const pc = LETTER[m[1].toLowerCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0);
  return 12 * (Number(m[3]) + 1) + pc;
}

/** Note name of a MIDI number, with sharps: 61 is c#4. */
export const nameOf = (midi) => `${SHARP_NAMES[((midi % 12) + 12) % 12]}${Math.floor(midi / 12) - 1}`;

/** Parses melody text (with or without its quotes). ok is false (with reasons) when a word is not understood: the editor then leaves the text alone. */
export function parseMelody(text) {
  const body = String(text ?? '').trim().replace(/^"([\s\S]*)"$/, '$1');
  const notes = [], marks = [], reasons = [];
  let at = 0, trailingRest = 0;
  for (const word of body.split(/\s+/).filter(Boolean)) {
    const note = NOTE.exec(word), rest = REST.exec(word);
    if (note) {
      const len = note[4] === undefined ? 1 : Number(note[4]);
      const midi = midiOf(note[1] + note[2] + note[3]);
      notes.push({ midi, at, len, vel: note[5] === undefined ? null : Number(note[5]) });
      at += len; trailingRest = 0;
    } else if (rest) {
      const len = rest[1] === undefined ? 1 : Number(rest[1]);
      at += len; trailingRest += len;
    } else if (MARK.test(word)) marks.push({ at, text: word.toLowerCase() });
    else reasons.push(`"${word}" is not a note, a rest or a dynamics marker`);
  }
  return { ok: reasons.length === 0, reasons, notes, marks, total: at, trailingRest };
}

/** The velocity a note sounds with: its own, else the level of the last marker word before it, else 96 (the crescendo ramps are not drawn). */
export function effectiveVelocity(note, marks) {
  if (note.vel != null) return note.vel;
  let v = DEFAULT_VELOCITY;
  for (const m of [...marks].sort((a, b) => a.at - b.at)) if (m.at <= note.at + 1e-9 && LEVELS[m.text] !== undefined) v = LEVELS[m.text];
  return v;
}

/** Melody text from the model: notes in order, rests for the gaps, marker words before the first note at or after them. `length` pads a closing rest when the text had one. */
export function serializeMelody({ notes, marks = [] }, { trailingRest = 0 } = {}) {
  const sorted = [...notes].sort((a, b) => a.at - b.at || a.midi - b.midi);
  const ms = [...marks].sort((a, b) => a.at - b.at);
  const out = [];
  let at = 0, mi = 0;
  for (const n of sorted) {
    while (mi < ms.length && ms[mi].at <= n.at + 1e-9) {
      if (ms[mi].at > at + 1e-9) { out.push(`r:${fmt(ms[mi].at - at)}`); at = ms[mi].at; }
      out.push(ms[mi].text); mi++;
    }
    if (n.at > at + 1e-9) out.push(`r:${fmt(n.at - at)}`);
    out.push(`${nameOf(n.midi)}:${fmt(n.len)}${n.vel != null ? `:${n.vel}` : ''}`);
    at = n.at + n.len;
  }
  for (; mi < ms.length; mi++) out.push(ms[mi].text);
  if (trailingRest > 0) out.push(`r:${fmt(trailingRest)}`);
  return out.join(' ');
}

/** True when a note starting at `at` for `len` beats would overlap a note of the list (the one named `except` does not count). */
export const overlaps = (notes, at, len, except = null) => notes.some((n) => n !== except && at < n.at + n.len - 1e-9 && at + len > n.at + 1e-9);

/** The longest a note can be made from `at` before the next note starts (up to `limit` beats). */
export function room(notes, at, limit, except = null) {
  let end = limit;
  for (const n of notes) if (n !== except && n.at >= at - 1e-9 && n.at < end) end = Math.max(at, n.at);
  return end - at;
}

// ---------- chords ----------

const QUALITIES = {
  '': 'major', maj: 'major', M: 'major', m: 'minor', min: 'minor', '-': 'minor', dim: 'dim', aug: 'aug', '+': 'aug', sus2: 'sus2', sus4: 'sus4', sus: 'sus4',
  7: 'dom7', maj7: 'maj7', M7: 'maj7', m7: 'min7', min7: 'min7', m7b5: 'min7b5', dim7: 'dim7', 6: 'maj6', m6: 'min6', 9: 'dom9', maj9: 'maj9', m9: 'min9', add9: 'add9',
  '7sus4': '7sus4', 5: 'power5', mMaj7: 'minMaj7', 11: 'dom11', 13: 'dom13',
};
export const QUALITY_CHOICES = [['', 'major'], ['m', 'minor'], ['7', '7'], ['maj7', 'maj7'], ['m7', 'm7'], ['sus2', 'sus2'], ['sus4', 'sus4'], ['dim', 'dim'], ['aug', 'aug'], ['6', '6'], ['9', '9'], ['add9', 'add9'], ['5', 'power']];
const CHORD = /^([A-Ga-g])([#b]?)([^/]*)(?:\/([A-Ga-g][#b]?))?$/;

/** One chord name as parts, or { raw } when it is not understood (it is kept as it is). */
export function parseChord(word) {
  const m = CHORD.exec(word);
  if (!m || QUALITIES[m[3]] === undefined) return { raw: word };
  return { root: m[1].toUpperCase() + m[2], quality: m[3], bass: m[4] ? m[4][0].toUpperCase() + m[4].slice(1) : '' };
}
export const chordName = (c) => (c.raw !== undefined ? c.raw : `${c.root}${c.quality}${c.bass ? `/${c.bass}` : ''}`);
const rootPc = (s) => { const m = /^([A-G])([#b]?)$/.exec(s); return m ? (LETTER[m[1].toLowerCase()] + (m[2] === '#' ? 1 : m[2] === 'b' ? -1 : 0) + 12) % 12 : 0; };
export const pitchClassOf = rootPc;

/** The pitch classes (0-11) of a chord, lowest first, for the keyboard picture (an inversion puts the bass note first). */
export function chordClasses(c) {
  if (c.raw !== undefined) return [];
  const id = QUALITIES[c.quality];
  const pitches = chordPitches(60 + rootPc(c.root), id) ?? [];
  const classes = [...new Set(pitches.map((p) => p % 12))];
  if (c.bass) { const b = rootPc(c.bass); return [b, ...classes.filter((x) => x !== b)]; }
  return classes;
}

/** Parses chords text into bars: each bar is a list of chords (a dot shares a bar). */
export function parseChords(text) {
  const body = String(text ?? '').trim().replace(/^"([\s\S]*)"$/, '$1');
  return body.split(/\s+/).filter(Boolean).map((word) => word.split('.').map(parseChord));
}
export const serializeChords = (bars) => bars.map((bar) => bar.map(chordName).join('.')).join(' ');

/** The Roman numeral of a chord in a key (a root pitch class and minor or major), for the label under the chord: i, V, VI ... */
export function roman(c, keyRoot, minor) {
  if (c.raw !== undefined) return '';
  const steps = (rootPc(c.root) - keyRoot + 12) % 12;
  const major = ['I', 'bII', 'II', 'bIII', 'III', 'IV', '#IV', 'V', 'bVI', 'VI', 'bVII', 'VII'];
  const minorSet = ['i', 'bII', 'ii', 'III', 'iv', 'iv', 'bV', 'v', 'VI', 'VI', 'VII', 'vii'];
  const table = minor ? minorSet : major;
  let r = table[steps];
  const isMinorChord = c.quality.startsWith('m') && !c.quality.startsWith('maj');
  if (isMinorChord) r = r.toLowerCase(); else if (c.quality === '' || c.quality === '7' || c.quality.startsWith('maj') || c.quality === 'sus4') r = r.toUpperCase();
  return r + (c.quality === '7' ? '7' : c.quality.startsWith('maj7') ? 'maj7' : '');
}

export { CHORDS };
