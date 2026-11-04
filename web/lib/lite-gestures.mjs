// Pure gesture maths of the Lite roll "AMPLE Jam" (plan MLS05/MLS06): move, resize, clone and delete of (tie-merged) rows, drawing with a
// duplicate guard, the sounding set for the lit notes, the audition throttle. No DOM. Every function takes a notes-model and returns a new one; a note
// that is not part of the gesture keeps its original entry text, and a moved or resized note keeps all its extension fields (pan, fine, slide, grp ...).
// A note longer than a bar is stored as tied bar segments (decision 5): rows here are the merged notes (mergeTies(absolute(model))). @ample:pianoroll-lite

import { absolute, addNote, entryText, format, noteChain, parse, quantizeNotes, resizeNote } from './notes-model.mjs';
import { mergeTies } from './live-synth.mjs';
import { clampToClip } from './lite-roll-model.mjs';
import { barBeats } from './meter.mjs';

/** The logical notes (bar-split tie chains merged); `index` is the index of the first segment in model.notes. */
export const mergedRows = (model) => mergeTies(absolute(model));

const keyOf = (at, pitch) => `${at}:${pitch}`;
const isNote = (item) => item.kind === 'note';

/** The note segments of one logical note starting at `at`; a tie id is given to every segment when it needs more than one bar. */
function segments(base, at, length, bar, tieId) {
  const { tie: _tie, bar: _bar, start: _start, length: _length, ...rest } = base;
  const out = [];
  for (let p = at; p < at + length;) {
    const b = Math.floor(p / bar), len = Math.min(at + length - p, bar - (p - b * bar));
    out.push({ ...rest, bar: b + 1, start: p - b * bar, length: len });
    p += len;
  }
  if (out.length > 1) { const id = tieId(); for (const note of out) note.tie = id; }
  return out;
}

/** Rebuilds the items: `replace` maps an item to new notes (written in its place, joined by single spaces), `drop` removes items with one neighbouring space, `append` adds notes at the end. */
function rebuild(model, { replace = new Map(), drop = new Set(), append = [] }) {
  const out = [], make = (note) => ({ kind: 'note', note, raw: entryText(note, model.ppq), touched: true });
  const src = model.items;
  for (let i = 0; i < src.length; i++) {
    const item = src[i];
    if (replace.has(item)) { replace.get(item).forEach((note, k) => { if (k) out.push({ kind: 'space', text: ' ' }); out.push(make(note)); }); continue; }
    if (drop.has(item)) {
      if (out.length && out[out.length - 1].kind === 'space') out.pop();
      else if (src[i + 1]?.kind === 'space') i++;
      continue;
    }
    out.push(item);
  }
  append.forEach((note) => { if (out.length && out[out.length - 1].kind !== 'space') out.push({ kind: 'space', text: ' ' }); out.push(make(note)); });
  return { ...model, items: out, notes: out.filter(isNote) };
}

/** Tie ids for the segments a gesture creates: the id a chain already has, else the smallest unused one. Returns a function (reuse) => () => id. */
function tieAllocator(model) {
  const used = new Set(model.notes.map((item) => item.note.tie).filter((t) => t !== undefined));
  let next = 0;
  return (reuse) => {
    if (reuse !== undefined) return () => reuse;
    let id = null;
    return () => { if (id === null) { while (used.has(next)) next++; id = next; used.add(next); } return id; };
  };
}

/** The shift a group move can really make: not before tick 0, not past the clip end, pitch kept in 0-127. Returns the clamped { dTick, dPitch }. */
export function clampMove(rows, dTick, dPitch, endTick) {
  if (!rows.length) return { dTick: 0, dPitch: 0 };
  const minAt = Math.min(...rows.map((r) => r.at)), maxEnd = Math.max(...rows.map((r) => r.at + r.length));
  const minPitch = Math.min(...rows.map((r) => r.pitch)), maxPitch = Math.max(...rows.map((r) => r.pitch));
  return {
    dTick: Math.round(Math.max(-minAt, Math.min(Math.max(0, endTick - maxEnd), dTick))) + 0,
    dPitch: Math.round(Math.max(-minPitch, Math.min(127 - maxPitch, dPitch))) + 0,
  };
}

/** The merged-row index (index of the first segment) of the logical note that owns segment `index`. */
const headIndex = (model, index) => model.notes.indexOf(noteChain(model, index).chain[0]);

/** Every start (ticks) at which a note of `length` placed at `at` has a piece: its own start and each bar line it crosses (tied pieces). */
export function pieceStarts(at, length, bar) {
  const out = [at];
  for (let p = (Math.floor(at / bar) + 1) * bar; p < at + length; p += bar) out.push(p);
  return out;
}

/** Map "at:pitch" -> head index for EVERY piece of every note, so the tail piece of a tied note counts as a note start (the duplicate guard spans the whole note). */
function pieceKeys(model) {
  const map = new Map();
  absolute(model).forEach((seg) => { const key = keyOf(seg.at, seg.pitch); if (!map.has(key)) map.set(key, headIndex(model, seg.index)); });
  return map;
}

/** Items of the notes (other than `skip`, merged-row indexes) with a piece exactly where a moved or cloned note lands: the moved note wins, so no duplicate stays. */
function covered(model, skip, landing) {
  const hit = new Set(), keys = new Set(landing.map((l) => keyOf(l.at, l.pitch)));
  for (const seg of absolute(model)) {
    if (!keys.has(keyOf(seg.at, seg.pitch))) continue;
    const head = headIndex(model, seg.index);
    if (skip.has(head)) continue;
    for (const item of noteChain(model, head).chain) hit.add(item);
  }
  return hit;
}

/** Landing keys of moved or cloned rows: every piece start, so a tail landing on another note's start is caught too. */
const landingOf = (rows, move, bar) => rows.flatMap((r) => pieceStarts(r.at + move.dTick, r.length, bar).map((at) => ({ at, pitch: r.pitch + move.dPitch })));

/**
 * Removes a note (all its pieces) whose piece starts where another note has a piece at the same pitch. Only notes this gesture wrote (`touched`) are ever dropped, the
 * first in the text wins among them, and an overlap that was already in the text (written elsewhere) stays. Bar lines are fixed, so a shift can bring two tails together.
 */
function dropPieceDuplicates(model) {
  const seen = new Map(), dupes = new Map(), touched = (head) => noteChain(model, head).chain.some((item) => item.touched);
  absolute(model).forEach((seg) => {
    const key = keyOf(seg.at, seg.pitch), head = headIndex(model, seg.index);
    if (!seen.has(key)) { seen.set(key, head); return; }
    const first = seen.get(key);
    if (first === head) return;
    if (touched(head)) dupes.set(head, { index: head });
    else if (touched(first)) { dupes.set(first, { index: first }); seen.set(key, head); }
  });
  return dupes.size ? removeRows(model, [...dupes.values()]) : model;
}

/** Moves the rows by dTick / dPitch (clamped, see clampMove). Segments are re-split at the bar lines; the order of the text and every field stay. */
export function moveRows(model, rows, dTick, dPitch, endTick) {
  const move = clampMove(rows, dTick, dPitch, endTick);
  if (!rows.length || (!move.dTick && !move.dPitch)) return model;
  const bar = barBeats() * model.ppq, tie = tieAllocator(model), replace = new Map(), drop = new Set(), skip = new Set(rows.map((r) => r.index));
  for (const row of rows) {
    const { chain } = noteChain(model, row.index);
    replace.set(chain[0], segments({ ...chain[0].note, pitch: row.pitch + move.dPitch }, row.at + move.dTick, row.length, bar, tie(chain[0].note.tie)));
    for (const item of chain.slice(1)) drop.add(item);
  }
  for (const item of covered(model, skip, landingOf(rows, move, bar))) drop.add(item);
  return dropPieceDuplicates(rebuild(model, { replace, drop }));
}

/** Copies the rows moved by dTick / dPitch (Shift+drag); the originals stay. A copy landing exactly on another note replaces it. */
export function cloneRows(model, rows, dTick, dPitch, endTick) {
  const move = clampMove(rows, dTick, dPitch, endTick);
  if (!rows.length || (!move.dTick && !move.dPitch)) return model;
  const bar = barBeats() * model.ppq, tie = tieAllocator(model), append = [];
  for (const row of rows) {
    const { chain } = noteChain(model, row.index);
    append.push(...segments({ ...chain[0].note, pitch: row.pitch + move.dPitch }, row.at + move.dTick, row.length, bar, tie()));
  }
  return dropPieceDuplicates(rebuild(model, { drop: covered(model, new Set(), landingOf(rows, move, bar)), append }));
}

/** The first start (a head or a tail piece) of ANOTHER note at the same pitch after `row.at`, or null: a growing note stops there instead of covering it. */
function nextPieceStart(model, row) {
  const own = new Set(noteChain(model, row.index).chain.map((item) => model.notes.indexOf(item)));
  let best = null;
  for (const seg of absolute(model)) if (seg.pitch === row.pitch && !own.has(seg.index) && seg.at > row.at && (best === null || seg.at < best)) best = seg.at;
  return best;
}

/** Resizes the right edge of every row by deltaLen ticks (at least 1 tick, not past the clip end). Bar-crossing notes become tied segments, shrinking merges them. */
export function resizeRows(model, rows, deltaLen, endTick) {
  const bar = barBeats() * model.ppq, tie = tieAllocator(model), replace = new Map(), drop = new Set();
  for (const row of rows) {
    let length = Math.max(1, Math.min(Math.round(row.length + deltaLen), Math.max(1, endTick - row.at)));
    const limit = nextPieceStart(model, row);
    if (deltaLen > 0 && length > row.length && limit !== null) length = Math.max(row.length, Math.min(length, limit - row.at));
    if (length === row.length) continue;
    const { chain } = noteChain(model, row.index);
    replace.set(chain[0], segments(chain[0].note, row.at, length, bar, tie(chain[0].note.tie)));
    for (const item of chain.slice(1)) drop.add(item);
  }
  return replace.size ? rebuild(model, { replace, drop }) : model;
}

/** Removes the rows (every segment of a tied note). */
export function removeRows(model, rows) {
  const drop = new Set();
  for (const row of rows) for (const item of noteChain(model, row.index).chain) drop.add(item);
  return drop.size ? rebuild(model, { drop }) : model;
}

/** The index of the note with a piece (head or tied tail) at exactly (at, pitch), or -1. */
export const rowIndexAt = (model, at, pitch) => pieceKeys(model).get(keyOf(at, pitch)) ?? -1;

/** A new note at tick `tick`, clipped to the clip end, tied across bar lines. Nothing happens when a note already sits at that tick and pitch (no duplicate). */
export function drawRow(model, tick, pitch, length, endTick, velocity = 96) {
  const c = tick >= endTick ? null : clampToClip(tick, length, endTick);
  if (!c || pitch < 0 || pitch > 127 || rowIndexAt(model, c.start, pitch) >= 0) return model;
  // a growing note stops before the next piece (head or tied tail) at its pitch, so no piece ever shares a start with another note
  const stop = absolute(model).filter((seg) => seg.pitch === pitch && seg.at > c.start).reduce((m, seg) => Math.min(m, seg.at), Infinity), len = Math.max(1, Math.min(c.length, stop - c.start));
  const bar = barBeats() * model.ppq, b = Math.floor(c.start / bar), start = c.start - b * bar, first = Math.min(len, bar - start);
  const next = addNote(model, { bar: b + 1, start, length: first, pitch, velocity });
  return len > first ? resizeNote(next, next.notes.length - 1, len) : next;
}

/** Cuts a row in two at `cutTick` like sliceNote, unless a note already starts there at that pitch. */
export function sliceRow(model, row, cutTick, slice) {
  if (rowIndexAt(model, cutTick, row.pitch) >= 0) return model;
  return slice(model, row, cutTick);
}

/** Sets the velocity of the rows (all segments of a tied note follow). */
export function velocityRows(model, rows, velocity) {
  const v = Math.max(1, Math.min(127, Math.round(velocity))), replace = new Map();
  for (const row of rows) for (const item of noteChain(model, row.index).chain) if (item.note.velocity !== v) replace.set(item, [{ ...item.note, velocity: v }]);
  return replace.size ? rebuild(model, { replace }) : model;
}

/** The indexes of the rows sounding at transport position `pos` (ticks): the lit notes in the grid. */
export function soundingRows(rows, pos) {
  const out = new Set();
  for (const row of rows) if (pos >= row.at && pos < row.at + row.length) out.add(row.index);
  return out;
}

/** The pitches of the sounding rows: the lit piano keys. */
export const soundingPitches = (rows, pos) => new Set(rows.filter((row) => pos >= row.at && pos < row.at + row.length).map((row) => row.pitch));

/** Allows an action at most once per `ms` (the re-audition while a note is dragged); `clock` is injectable for tests. */
export function createThrottle(ms, clock = () => performance.now()) {
  let last = -Infinity;
  return () => { const now = clock(); if (now - last < ms) return false; last = now; return true; };
}

// ---- clipboard, duplicate, quick quantize and per-note adjustments (MLS12) ----

/** The clipboard items of the rows: absolute start, length, pitch and EVERY other field of the note (pan, fine, grp ...), so a paste keeps what Lite does not show. */
export function copyItems(model, rows) {
  return rows.map((row) => {
    const { chain } = noteChain(model, row.index), { bar: _b, start: _s, length: _l, tie: _t, ...rest } = chain[0].note;
    return { at: row.at, length: row.length, note: rest };
  });
}

/** Pastes the clipboard items so the earliest one starts at `tick` (clamped inside the clip); each is tied across bar lines. A paste landing exactly on another note replaces it. */
export function pasteRows(model, items, tick, endTick, dPitch = 0) {
  if (!items.length) return model;
  const first = Math.min(...items.map((i) => i.at)), last = Math.max(...items.map((i) => i.at + i.length));
  const start = Math.max(0, Math.min(Math.round(tick), Math.max(0, endTick - (last - first)))), shift = start - first;
  const pitchShift = Math.max(-Math.min(...items.map((i) => i.note.pitch)), Math.min(127 - Math.max(...items.map((i) => i.note.pitch)), dPitch));
  const bar = barBeats() * model.ppq, tie = tieAllocator(model), append = [];
  for (const item of items) append.push(...segments({ ...item.note, pitch: item.note.pitch + pitchShift }, item.at + shift, Math.min(item.length, Math.max(1, endTick - (item.at + shift))), bar, tie()));
  const landing = items.flatMap((i) => pieceStarts(i.at + shift, Math.min(i.length, Math.max(1, endTick - (i.at + shift))), bar).map((at) => ({ at, pitch: i.note.pitch + pitchShift })));
  return dropPieceDuplicates(rebuild(model, { drop: covered(model, new Set(), landing), append }));
}

/** Ctrl+B: copies the rows right behind themselves (FL: "duplicate selection to the right"). */
export function duplicateRows(model, rows, endTick) {
  if (!rows.length) return model;
  const first = Math.min(...rows.map((r) => r.at)), last = Math.max(...rows.map((r) => r.at + r.length));
  if (last - first + last > endTick) return model;
  return pasteRows(model, copyItems(model, rows), last, endTick);
}

/**
 * Quick quantize (Ctrl+Q): the starts of the rows go to the nearest multiple of `gridTicks` through quantizeNotes (single notes) and moveRows (tied notes, so
 * the chain stays whole). A note that would then end past the clip end stays where it is; two notes landing on the same start and pitch become one (the first
 * wins). Quantizing twice gives the same text as once.
 */
export function quantizeRows(model, rows, gridTicks, endTick) {
  if (!rows.length || !(gridTicks > 0)) return model;
  const snapped = (row) => Math.round(row.at / gridTicks) * gridTicks;
  const ok = rows.filter((row) => snapped(row) !== row.at && snapped(row) + row.length <= endTick);
  const single = ok.filter((row) => model.notes[row.index].note.tie === undefined), tied = ok.filter((row) => model.notes[row.index].note.tie !== undefined);
  let next = single.length ? quantizeNotes(model, single.map((r) => r.index), gridTicks) : model;
  for (const row of [...tied].sort((a, b) => b.index - a.index)) {
    const fresh = mergedRows(next).find((r) => r.index === row.index);
    if (fresh) next = moveRows(next, [fresh], snapped(row) - fresh.at, 0, endTick);
  }
  return dropPieceDuplicates(next);
}

/** Changes fields of the rows (all segments of a tied note): `change(note)` returns the changed fields. The velocity stays 1-127 and fine -100..100. */
export function adjustRows(model, rows, change) {
  const replace = new Map();
  for (const row of rows) for (const item of noteChain(model, row.index).chain) {
    const next = { ...item.note, ...change(item.note) };
    if (next.velocity !== undefined) next.velocity = Math.max(1, Math.min(127, Math.round(next.velocity)));
    if (next.fine !== undefined) next.fine = Math.max(-100, Math.min(100, Math.round(next.fine)));
    if (JSON.stringify(next) !== JSON.stringify(item.note)) replace.set(item, [next]);
  }
  return replace.size ? rebuild(model, { replace }) : model;
}

// ---- the MIDI clipboard (Shift+Ctrl+V): notes as plain text on the system clipboard ----

/** The clipboard items as compact note text (the text format the Advanced roll also writes): one entry per piece, tied across bar lines, every field kept. */
export function clipboardText(items, ppq) {
  if (!items.length) return '';
  const first = Math.min(...items.map((i) => i.at));
  return format(pasteRows(parse('', ppq), items, first, Infinity)).trim();
}

/** Reads note text back into clipboard items; anything that is not plain note entries (other text, expanded notes, chords) gives []. Never throws. */
export function itemsFromText(text, ppq) {
  if (typeof text !== 'string' || !text.trim() || text.length > 200_000) return [];
  const model = parse(text.trim().replace(/^"([\s\S]*)"$/, '$1'), ppq);
  if (!model.notes.length || !model.items.every((item) => item.kind === 'note' || item.kind === 'space')) return [];
  const rows = mergedRows(model);
  return rows.length ? copyItems(model, rows) : [];
}
