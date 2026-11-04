// The lossless notes model of the piano roll (plan G01). It reads the compact notes text of a midi-clip (the `notes` parameter: entries separated by white space,
// each bar:start:length:pitch:velocity[:channel:bank:program[:tie]]) into note objects and writes it back WITHOUT losing anything: the white space between
// entries, entries it does not understand (kept as raw items) and the quotes around the value are kept exactly, and a note that was not touched is written
// with its original text. format(parse(text)) === text for every text. The text stays the truth; the roll is only a way to edit it.
// Twin of web/lib/expanded-midi-notes.js (the validation) and of the engine reader. @ample:notes-model

import { barBeats } from './meter.mjs';
export const MAX_NOTES = 100_000;
export const MAX_TEXT = 4_000_000;

const PLAIN = /^\d+$/;

// PR06: the per-note extension suffix, `;key=value` pairs after the base entry (bar:start:length:pitch:velocity[:ch:bank:prog[:tie]]).
// A value equal to the default is never written, so old clips stay byte-identical until a property is actually used. Unknown keys are kept
// verbatim (in `note.extra`, appended after the known keys on write) so a text written by a newer or different reader is never dropped.
// `grp` (PR18, colour groups 1-8) is a roll-only convention, not part of PR00's acoustic grammar, but it round-trips the same way and the
// engine already treats any key it does not name as "unknown but must be a valid integer" (StructuredMidiNotes.cs), so no engine change is needed.
const EXT_KEYS = ['pan', 'fine', 'rel', 'cut', 'slide', 'port', 'bend', 'grp', 'tie'];
const EXT_RANGES = { pan: [-100, 100], fine: [-100, 100], rel: [0, 5000], cut: [0, 100], slide: [0, 1], port: [0, 2000], bend: [-2400, 2400], grp: [1, 8], tie: [0, 2_000_000] };
const EXT_DEFAULT = { pan: 0, fine: 0, cut: 100, slide: 0, port: 0, bend: 0 };   // rel and grp have no fixed default: never suppressed

/** Reads the `;key=value` pairs after a note entry. Returns null when a known key has an invalid or out-of-range value (the whole entry is then
 * raw, never repaired silently); unknown keys need only a valid integer and are kept as opaque text in the returned `extra`. */
function parseExtensions(text) {
  const known = {}, extraParts = [], seen = new Set();
  for (const pair of text.split(';')) {
    const m = /^([a-z]+)=(-?\d+)$/.exec(pair);
    if (!m) return null;
    const [, key, valueText] = m, value = Number(valueText);
    if (EXT_KEYS.includes(key)) {
      if (seen.has(key)) return null;
      seen.add(key);
      const [lo, hi] = EXT_RANGES[key];
      if (value < lo || value > hi) return null;
      known[key] = value;
    } else extraParts.push(pair);
  }
  return { known, extra: extraParts.length ? `;${extraParts.join(';')}` : '' };
}

/** One time value as an integer: whole ticks, or (with ppq 1,000,000, the default microbeats) beats with up to six decimals. */
function timeValue(text, ppq) {
  if (ppq !== 1_000_000) return PLAIN.test(text) && Number.isSafeInteger(Number(text)) ? Number(text) : null;
  if (!/^(0|[1-9]\d*)(\.\d{1,6})?$/.test(text)) return null;
  const [whole, fraction = ''] = text.split('.');
  const ticks = Number(whole) * ppq + Number((fraction + '000000').slice(0, 6));
  return Number.isSafeInteger(ticks) ? ticks : null;
}

/** The text of a time value: whole ticks, or beats with the shortest exact decimals when ppq is 1,000,000. */
function timeText(ticks, ppq) {
  if (ppq !== 1_000_000) return String(ticks);
  const whole = Math.floor(ticks / ppq), fraction = String(ticks % ppq).padStart(6, '0').replace(/0+$/, '');
  return fraction ? `${whole}.${fraction}` : String(whole);
}

/** Reads one entry; null when it is not a valid note for this ppq (then it is kept as raw). */
export function parseEntry(entry, ppq) {
  const semi = entry.indexOf(';');
  const basePart = semi < 0 ? entry : entry.slice(0, semi);
  const f = basePart.split(':');
  if (![5, 8, 9].includes(f.length)) return null;
  const [barText, startText, lengthText, pitchText, velocityText, channelText, bankText, programText, tieText] = f;
  const bar = Number(barText), pitch = Number(pitchText), velocity = Number(velocityText);
  const start = timeValue(startText, ppq), length = timeValue(lengthText, ppq);
  if (!PLAIN.test(barText) || bar < 1 || bar > 1_000_000 || start === null || length === null || length < 1 || start >= barBeats() * ppq || length > barBeats() * ppq - start) return null;
  if (!PLAIN.test(pitchText) || pitch > 127 || !PLAIN.test(velocityText) || velocity < 1 || velocity > 127) return null;
  const note = { bar, start, length, pitch, velocity };
  if (f.length >= 8) {
    if (![channelText, bankText, programText].every((x) => PLAIN.test(x)) || Number(channelText) > 15 || Number(bankText) > 16383 || Number(programText) > 127) return null;
    Object.assign(note, { channel: Number(channelText), bank: Number(bankText), program: Number(programText) });
  }
  if (f.length === 9) {
    if (!PLAIN.test(tieText) || Number(tieText) > 2_000_000) return null;
    note.tie = Number(tieText);
  }
  if (semi >= 0) {
    const ext = parseExtensions(entry.slice(semi + 1));
    if (!ext) return null;
    if (note.tie !== undefined && ext.known.tie !== undefined) return null;
    Object.assign(note, ext.known);
    if (ext.extra) note.extra = ext.extra;
  }
  return note;
}

/** The canonical text of a note. */
export function entryText(note, ppq) {
  const fields = [String(note.bar), timeText(note.start, ppq), timeText(note.length, ppq), String(note.pitch), String(note.velocity)];
  if (note.channel !== undefined) fields.push(String(note.channel), String(note.bank ?? 0), String(note.program ?? 0));
  if (note.tie !== undefined && note.channel !== undefined) fields.push(String(note.tie));
  let text = fields.join(':');
  for (const key of EXT_KEYS) {
    if (note[key] === undefined) continue;
    if (key === 'tie' && note.channel !== undefined) continue;
    if (key in EXT_DEFAULT && note[key] === EXT_DEFAULT[key]) continue;
    text += `;${key}=${note[key]}`;
  }
  if (note.extra) text += note.extra;
  return text;
}

const unrepresentable = (ppq, reason) => ({ ppq, quote: '', lead: '', trail: '', items: [], notes: [], representable: false, reasons: [reason] });

/**
 * Reads a notes text. Returns { ppq, quote, lead, trail, items, notes, representable, reasons }.
 * items, in order: { kind: 'space', text } | { kind: 'note', note, raw } | { kind: 'raw', text }; `notes` lists the note items.
 * A text that is not the compact form (for example the expanded `notes {` form) gives no items and representable false with a reason.
 */
export function parse(text, ppq = 1_000_000) {
  if (typeof text !== 'string') return unrepresentable(ppq, 'the notes text is missing');
  if (text.length > MAX_TEXT) return unrepresentable(ppq, `the text is longer than ${MAX_TEXT} characters`);
  if (/^\s*notes\s*\{/.test(text)) return unrepresentable(ppq, 'the expanded form (notes { note { ... } }) is edited in code');
  const reasons = [];
  let body = text, quote = '', lead = '', trail = '';
  const q = /^(\s*)"([\s\S]*)"(\s*)$/.exec(text);
  if (q) { quote = '"'; lead = q[1]; body = q[2]; trail = q[3]; }
  const items = [], notes = [];
  for (const part of body.split(/(\s+)/)) {
    if (part === '') continue;
    if (/^\s+$/.test(part)) { items.push({ kind: 'space', text: part }); continue; }
    const note = parseEntry(part, ppq);
    if (note) { const item = { kind: 'note', note, raw: part }; items.push(item); notes.push(item); }
    else { items.push({ kind: 'raw', text: part }); reasons.push(`"${part.length > 24 ? `${part.slice(0, 24)}...` : part}" is not a note the roll can show`); }
  }
  if (notes.length > MAX_NOTES) reasons.push(`more than ${MAX_NOTES} notes`);
  return { ppq, quote, lead, trail, items, notes, representable: reasons.length === 0, reasons };
}

/** Writes the text back. A note that was not touched keeps its original entry text. */
export function format(model) {
  const body = model.items.map((item) => (item.kind === 'note' ? (item.touched ? entryText(item.note, model.ppq) : item.raw) : item.text)).join('');
  return model.quote ? `${model.lead}${model.quote}${body}${model.quote}${model.trail}` : body;
}

const withNote = (item, note) => ({ ...item, note, touched: true });

/** A new model with the note at `index` (in the order of `notes`) changed. Only that entry changes in the text. */
export function updateNote(model, index, changes) {
  const target = model.notes[index];
  if (!target) throw new RangeError('no such note');
  return updateMany(model, new Map([[index, changes]]));
}

/** A new model with several notes changed in one pass; `changes` is a Map from the index in `notes` to the changed fields. */
export function updateMany(model, changes) {
  if (!changes.size) return model;
  const edits = new Map(changes);
  const expression = ['pitch', 'velocity', 'pan', 'fine', 'rel', 'cut', 'slide', 'port', 'grp'];
  for (const [index, change] of changes) {
    if (model.notes[index]?.note.tie === undefined) continue;
    const fields = Object.fromEntries(Object.entries(change).filter(([key]) => expression.includes(key)));
    if (!Object.keys(fields).length) continue;
    for (const item of noteChain(model, index).chain) {
      const part = model.notes.indexOf(item);
      edits.set(part, { ...fields, ...edits.get(part) });
    }
  }
  const replaced = new Map();
  model.notes.forEach((item, index) => { if (edits.has(index)) replaced.set(item, withNote(item, { ...item.note, ...edits.get(index) })); });
  return { ...model, items: model.items.map((item) => replaced.get(item) ?? item), notes: model.notes.map((item) => replaced.get(item) ?? item) };
}

/** A new model with a note added at the end (after one space). */
export function addNote(model, note) {
  if (model.notes.length >= MAX_NOTES) throw new RangeError(`at most ${MAX_NOTES} notes`);
  const item = { kind: 'note', note, raw: entryText(note, model.ppq), touched: true };
  const needsSpace = model.items.length > 0 && model.items[model.items.length - 1].kind !== 'space';
  return { ...model, items: [...model.items, ...(needsSpace ? [{ kind: 'space', text: ' ' }] : []), item], notes: [...model.notes, item] };
}

/** A new model without the note at `index`; the space before it goes with it (or the space after, for the first entry). */
export function removeNote(model, index) {
  const target = model.notes[index];
  if (!target) throw new RangeError('no such note');
  if (target.note.tie !== undefined) {
    const chain = noteChain(model, index).chain;
    const items = [...model.items];
    for (const item of [...chain].sort((a, b) => items.indexOf(b) - items.indexOf(a))) {
      const at = items.indexOf(item), before = items[at - 1]?.kind === 'space', after = items[at + 1]?.kind === 'space';
      items.splice(before ? at - 1 : at, before || after ? 2 : 1);
    }
    return { ...model, items, notes: items.filter((item) => item.kind === 'note') };
  }
  const at = model.items.indexOf(target);
  const items = [...model.items];
  const before = items[at - 1]?.kind === 'space', after = items[at + 1]?.kind === 'space';
  items.splice(before ? at - 1 : at, before || after ? 2 : 1);
  return { ...model, items, notes: model.notes.filter((item) => item !== target) };
}

/** Moves a note in time and pitch (ticks and semitones), staying inside its bar: the start is clamped and the length shortened when it would pass the bar line. */
export function moveNote(model, index, dStart, dPitch) {
  const n = model.notes[index].note, bar = barBeats() * model.ppq;
  const start = Math.max(0, Math.min(bar - 1, n.start + dStart));
  return updateNote(model, index, { start, length: Math.min(n.length, bar - start), pitch: Math.max(0, Math.min(127, n.pitch + dPitch)) });
}

/** Moves several notes at once (see moveNote), in one pass. */
export function moveNotes(model, indexes, dStart, dPitch) {
  const bar = barBeats() * model.ppq, changes = new Map();
  for (const i of indexes) {
    const n = model.notes[i].note, start = Math.max(0, Math.min(bar - 1, n.start + dStart));
    changes.set(i, { start, length: Math.min(n.length, bar - start), pitch: Math.max(0, Math.min(127, n.pitch + dPitch)) });
  }
  return updateMany(model, changes);
}

export function noteChain(model, index) {
  const target = model.notes[index];
  if (!target) throw new RangeError('no such note');
  const n = target.note, barTicks = barBeats() * model.ppq;
  const sameVoice = (note) => note.tie === n.tie && note.pitch === n.pitch && (note.channel ?? 0) === (n.channel ?? 0) && (note.bank ?? 0) === (n.bank ?? 0) && (note.program ?? 0) === (n.program ?? 0);
  const candidates = n.tie === undefined ? [target] : model.notes.filter((item) => sameVoice(item.note)).sort((a, b) => a.note.bar - b.note.bar || a.note.start - b.note.start);
  const chain = [target];
  let start = (n.bar - 1) * barTicks + n.start, end = start + n.length;
  for (let i = candidates.indexOf(target) - 1; i >= 0; i--) {
    const item = candidates[i], at = (item.note.bar - 1) * barTicks + item.note.start;
    if (at + item.note.length !== start) break;
    chain.unshift(item); start = at;
  }
  for (let i = candidates.indexOf(target) + 1; i < candidates.length; i++) {
    const item = candidates[i], at = (item.note.bar - 1) * barTicks + item.note.start;
    if (at !== end) break;
    chain.push(item); end = at + item.note.length;
  }
  return { chain, start, end };
}

/** Resize a logical note, storing bar-crossing notes as contiguous tied entries accepted by the engine. */
export function resizeNote(model, index, length) {
  const n = model.notes[index]?.note;
  if (!n) throw new RangeError('no such note');
  const barTicks = barBeats() * model.ppq, { chain, start } = noteChain(model, index);
  const ticks = Math.max(1, Math.round(length));
  if (!Number.isSafeInteger(ticks) || start + ticks > 1_000_000 * barTicks) throw new RangeError('note length exceeds the supported timeline');
  const pieces = Math.ceil(((start % barTicks) + ticks) / barTicks);
  if (model.notes.length - chain.length + pieces > MAX_NOTES) throw new RangeError(`at most ${MAX_NOTES} notes`);
  const ids = new Set(model.notes.map((item) => item.note.tie).filter((id) => id !== undefined));
  let tie = n.tie ?? 0;
  while (n.tie === undefined && ids.has(tie)) tie++;
  if (tie > 2_000_000) throw new RangeError('no free tie id');
  const added = [];
  for (let at = start; at < start + ticks;) {
    const note = { ...chain[0].note, bar: Math.floor(at / barTicks) + 1, start: at % barTicks, length: Math.min(start + ticks - at, barTicks - at % barTicks) };
    if (pieces > 1) note.tie = tie;
    else delete note.tie;
    added.push({ kind: 'note', note, raw: entryText(note, model.ppq), touched: true });
    at += note.length;
  }
  const removed = new Set(chain), items = model.items.flatMap((item) => item === chain[0] ? added.flatMap((piece, i) => i ? [{ kind: 'space', text: ' ' }, piece] : [piece]) : removed.has(item) ? [] : [item]);
  return { ...model, items, notes: items.filter((item) => item.kind === 'note') };
}

/** Sets the velocity (1 to 127). */
export const setVelocity = (model, index, velocity) => updateNote(model, index, { velocity: Math.max(1, Math.min(127, Math.round(velocity))) });

/** Moves the given notes by semitones. */
export function transposeNotes(model, indexes, semitones) {
  return updateMany(model, new Map(indexes.map((i) => [i, { pitch: Math.max(0, Math.min(127, model.notes[i].note.pitch + semitones)) }])));
}

/** Snaps the start of the given notes to a grid in ticks (a note that would then run past the bar line is shortened). */
export function quantizeNotes(model, indexes, gridTicks) {
  const bar = barBeats() * model.ppq, changes = new Map();
  for (const i of indexes) {
    const n = model.notes[i].note, start = Math.min(bar - 1, Math.round(n.start / gridTicks) * gridTicks);
    changes.set(i, { start, length: Math.min(n.length, bar - start) });
  }
  return updateMany(model, changes);
}

/** The notes as plain objects with an absolute start in ticks since bar 1 (for drawing and sorting). */
export const absolute = (model) => model.notes.map((item, index) => ({ index, ...item.note, at: (item.note.bar - 1) * barBeats() * model.ppq + item.note.start }));

/**
 * Adds notes given with an absolute start (ticks since bar 1) and a length: each is placed in the bar it starts in and its length is shortened at the bar line.
 * `shift` moves them all in time first, `pitchShift` in pitch. Returns the new model; the added notes are the last ones of `notes`.
 */
export function pasteNotes(model, items, shift = 0, pitchShift = 0) {
  if (model.notes.length + items.length > MAX_NOTES) throw new RangeError(`at most ${MAX_NOTES} notes`);
  const bar = barBeats() * model.ppq, added = [], extra = [];
  const needsSpace = model.items.length > 0 && model.items[model.items.length - 1].kind !== 'space';
  items.forEach((n, k) => {
    const at = Math.max(0, n.at + shift), b = Math.floor(at / bar), start = at - b * bar;
    const note = { bar: b + 1, start, length: Math.max(1, Math.min(n.length, bar - start)), pitch: Math.max(0, Math.min(127, n.pitch + pitchShift)), velocity: n.velocity };
    for (const key of ['channel', 'bank', 'program']) if (n[key] !== undefined) note[key] = n[key];
    if (Number.isFinite(n.bend) && n.bend !== 0) note.bend = Math.max(-2400, Math.min(2400, Math.round(n.bend)));
    const item = { kind: 'note', note, raw: entryText(note, model.ppq), touched: true };
    if (k > 0 || needsSpace) extra.push({ kind: 'space', text: ' ' });
    extra.push(item); added.push(item);
  });
  return { ...model, items: [...model.items, ...extra], notes: [...model.notes, ...added] };
}

/**
 * Turns played notes into notes for the model. `played` is a list of { on, off, pitch, velocity } with times in milliseconds since the recording started,
 * `bpm` the tempo, `from` the absolute tick where the recording starts. With a grid (ticks) the starts are snapped to it and a note is at least one grid step long.
 */
export function recordedToNotes(played, bpm, ppq, from = 0, grid = 0) {
  const ticks = (ms) => (ms / 1000) * (bpm / 60) * ppq;
  return played.map((p) => {
    let at = from + ticks(p.on), end = from + ticks(p.off);
    if (grid > 0) { at = Math.round(at / grid) * grid; end = Math.max(at + grid, Math.round(end / grid) * grid); }
    at = Math.max(0, Math.round(at));
    return { at, length: Math.max(1, Math.round(end - at)), pitch: p.pitch, velocity: Math.max(1, Math.min(127, p.velocity)) };
  }).sort((a, b) => a.at - b.at || a.pitch - b.pitch);
}

/** The pitch a computer key plays in step input: two rows of keys as a piano (a w s e d f t g y h u j k o l), relative to a base octave. */
export const KEY_PITCH = { a: 0, w: 1, s: 2, e: 3, d: 4, f: 5, t: 6, g: 7, y: 8, h: 9, u: 10, j: 11, k: 12, o: 13, l: 14 };
export const keyToPitch = (key, octave) => (key in KEY_PITCH ? Math.max(0, Math.min(127, 12 * (octave + 1) + KEY_PITCH[key])) : null);
