// Pure view model of the Lite roll "AMPLE Jam" (plan MLS04): tools, snap, grid levels, loop bracket gestures, velocity strip maths,
// transport range, metronome clicks and the serialized commit queue. No DOM; web/app/pianoroll-lite.js draws and wires it. @ample:pianoroll-lite

import { addNote, resizeNote } from './notes-model.mjs';
import { barBeats } from './meter.mjs';

/** Toolbar order of decisions 7, 8 and 13 for the controls that exist in this slice (Roll/Grid and Simplify arrive with MLS10 and MLS07). */
export const TOOLBAR_ORDER = ['advanced', 'view', 'tools', 'typing', 'play', 'playSel', 'stop', 'metronome', 'loop', 'follow', 'snap', 'zoomOut', 'zoomIn', 'fit'];

export const TOOLS = [
  { id: 'draw', key: 'p', label: 'Draw', icon: '✏' },
  { id: 'paint', key: 'b', label: 'Paint', icon: '▦' },
  { id: 'delete', key: 'd', label: 'Delete', icon: '✕' },
  { id: 'slice', key: 'c', label: 'Slice', icon: '✂' },
  { id: 'select', key: 'e', label: 'Select', icon: '⬚' },
  { id: 'playback', key: 'y', label: 'Playback', icon: '⏵' },
  { id: 'zoom', key: 'z', label: 'Zoom', icon: '⌕' },
];
export const toolForKey = (key) => TOOLS.find((tool) => tool.key === String(key).toLowerCase())?.id ?? null;

/** Snap menu: Auto follows the zoom (decision 25), the rest are fixed. `beats` is the grid in beats (a quarter note), 0 is off. */
export const SNAPS = [
  { id: 'auto', label: 'Auto', beats: null },
  { id: 'bar', label: 'Bar', get beats() { return barBeats(); } },   // a bar of the open song (4 beats unless it has another meter)
  { id: '1/4', label: '1/4', beats: 1 },
  { id: '1/8', label: '1/8', beats: 0.5 },
  { id: '1/16', label: '1/16', beats: 0.25 },
  { id: '1/32', label: '1/32', beats: 0.125 },
  { id: '1/8t', label: '1/8 triplet', beats: 1 / 3 },
  { id: '1/16t', label: '1/16 triplet', beats: 1 / 6 },
  { id: 'off', label: 'None', beats: 0 },
];

/** The finest grid (in beats) whose lines are still at least `minPx` apart. */
export function autoSnapBeats(pxBeat, minPx = 12) {
  for (const beats of [0.125, 0.25, 0.5, 1, barBeats()]) if (beats * pxBeat >= minPx) return beats;
  return barBeats();
}

export function snapBeats(id, pxBeat) {
  const found = SNAPS.find((snap) => snap.id === id) ?? SNAPS[0];
  return found.beats === null ? autoSnapBeats(pxBeat) : found.beats;
}

/** Snap a tick to the grid; `bypass` (Alt held) or an off grid only rounds to a whole tick. */
export function snapTick(tick, beats, ppq, bypass = false) {
  const grid = beats * ppq;
  if (bypass || !(grid > 0)) return Math.round(tick);
  return Math.round(Math.round(tick / grid) * grid);
}

/** Grid line levels to draw: only those at least 8 px apart, fading in over the next 8 px (decision 25). kind gives bar > beat > step weight. */
export function gridLevels(pxBeat, barLength = barBeats()) {
  const out = [];
  for (const [beats, kind] of [[barLength, 'bar'], [1, 'beat'], [0.5, 'step'], [0.25, 'step'], [0.125, 'step']]) {
    const px = beats * pxBeat;
    if (px < 8) continue;
    out.push({ beats, kind, alpha: Math.min(1, (px - 8) / 8) });
  }
  return out;
}

/** The clip's end in bars: the arrangement length when known, never less than the notes need, plus the bars the user added with the end marker. */
export function clipEndBars(explicitBars, rows, ppq, extraBars = 0) {
  const bar = barBeats() * ppq, extent = rows.reduce((last, row) => Math.max(last, row.at + row.length), 0);
  return Math.max(Number(explicitBars) || 0, Math.ceil(extent / bar), 1) + Math.max(0, extraBars);
}

/** Keep a note inside the clip: start inside [0, end), length shortened so start + length <= end. Null when no room is left. */
export function clampToClip(start, length, endTick) {
  const at = Math.max(0, Math.min(endTick - 1, Math.round(start)));
  const len = Math.min(Math.max(1, Math.round(length)), endTick - at);
  return len >= 1 && endTick > 0 ? { start: at, length: len } : null;
}

/** True for a note that carries data Lite does not show (decision 2): a corner mark is drawn for it. */
export const hasHiddenData = (note) => ['slide', 'port'].some((key) => note[key] !== undefined && note[key] !== 0 && note[key] !== false);

// ---- typing keyboard (decision 11) ----

const LOWER = 'zsxdcvgbhnjm,./', UPPER = 'q2w3er5t6y7u';
/** MIDI pitch for a typing-keyboard key at an octave (4 puts Z on C4 = 60), or null. Lower row C..D one octave up, upper row the octave above. */
export function typingPitch(key, octave) {
  const k = String(key).toLowerCase(), low = LOWER.indexOf(k), up = UPPER.indexOf(k);
  const semis = low >= 0 ? low : up >= 0 ? 12 + up : -1;
  if (semis < 0) return null;
  const pitch = 12 * (octave + 1) + semis;
  return pitch >= 0 && pitch <= 127 ? pitch : null;
}

// ---- slice (decision 24) ----

/** Cut a (merged) row in two at `cutTick`: the first part is shortened, the second is a new note with the same fields. One model, one undo step. */
export function sliceNote(model, row, cutTick) {
  const end = row.at + row.length, bar = barBeats() * model.ppq;
  if (!(cutTick > row.at && cutTick < end)) return model;
  const shortened = resizeNote(model, row.index, cutTick - row.at);
  const { index, at, bar: _bar, start: _start, length: _length, tie: _tie, ...rest } = row;
  const b = Math.floor(cutTick / bar), start = cutTick - b * bar;
  const added = addNote(shortened, { ...rest, bar: b + 1, start, length: Math.min(end - cutTick, bar - start) });
  return resizeNote(added, added.notes.length - 1, end - cutTick);
}

// ---- loop bracket (decision 7, option A) ----

/** A valid bracket {start, end} with 0 <= start < end <= endTick, or null when it is shorter than `min` ticks. */
export function normalizeLoop(a, b, endTick, min = 1) {
  const start = Math.max(0, Math.min(a, b)), end = Math.min(endTick, Math.max(a, b));
  return end - start >= min && start < end ? { start, end } : null;
}

/** Which part of the bracket is at `tick`: its 'start' or 'end' edge (within `tol` ticks), its 'body', or null. */
export function hitLoop(loop, tick, tol) {
  if (!loop) return null;
  if (Math.abs(tick - loop.start) <= tol) return 'start';
  if (Math.abs(tick - loop.end) <= tol) return 'end';
  return tick > loop.start && tick < loop.end ? 'body' : null;
}

/**
 * The bracket while dragging. kind: 'create' (anchor is where the drag began), 'start' / 'end' (an edge), 'move' (anchor is the grab point).
 * `snap(tick)` rounds to the grid; `min` is the shortest bracket.
 */
export function dragLoop(kind, anchor, tick, base, endTick, snap, min = 1) {
  const t = Math.max(0, Math.min(endTick, snap(tick)));
  if (kind === 'create') return normalizeLoop(snap(anchor), t, endTick, min);
  if (!base) return null;
  if (kind === 'start') return { start: Math.max(0, Math.min(t, base.end - min)), end: base.end };
  if (kind === 'end') return { start: base.start, end: Math.min(endTick, Math.max(t, base.start + min)) };
  const length = base.end - base.start, start = Math.max(0, Math.min(endTick - length, snap(base.start + tick - anchor)));
  return { start, end: start + length };
}

// ---- velocity strip (decision 8) ----

/** Velocity (1-127) for a pointer height in the strip. */
export const velocityFromY = (y, height) => Math.max(1, Math.min(127, Math.round(127 * (1 - y / Math.max(1, height)))));

/** Velocities along a ramp from (tickA, velA) to (tickB, velB) for every row that starts between them: Map(index -> velocity). */
export function velocityRamp(rows, tickA, velA, tickB, velB) {
  const [t0, v0, t1, v1] = tickA <= tickB ? [tickA, velA, tickB, velB] : [tickB, velB, tickA, velA];
  const out = new Map();
  for (const row of rows) {
    if (row.at < t0 || row.at > t1) continue;
    const f = t1 === t0 ? 1 : (row.at - t0) / (t1 - t0);
    out.set(row.index, Math.max(1, Math.min(127, Math.round(v0 + (v1 - v0) * f))));
  }
  return out;
}

/** The row whose start is nearest `tick` within `tolTicks` (the bar a velocity drag grabs), or null. */
export function nearestStart(rows, tick, tolTicks) {
  let best = null, gap = tolTicks + 1;
  for (const row of rows) { const d = Math.abs(row.at - tick); if (d < gap) { gap = d; best = row; } }
  return gap <= tolTicks ? best : null;
}

// ---- transport (decision 4 and 26) ----

/**
 * Where playback starts and loops. The playhead decides, never the selection. With loop on and a bracket, a playhead outside the bracket starts at its
 * start; with loop on and no bracket the whole clip loops. Ticks; loopEnd 0 means no loop.
 */
export function litePlayRange(playhead, loopOn, bracket, endTick) {
  const head = playhead >= 0 && playhead < endTick ? playhead : 0;
  if (!loopOn) return { from: head, loopStart: 0, loopEnd: 0, end: endTick };
  const loop = bracket ?? { start: 0, end: endTick };
  const from = head >= loop.start && head < loop.end ? head : loop.start;
  return { from, loopStart: loop.start, loopEnd: loop.end, end: endTick };
}

/** Metronome clicks in the next `window` ticks from `pos`, as offsets in ticks; wraps at `loopEnd` back to `loopStart` when looping. Beat 1 of a bar is the accent. */
export function metronomeClicks(pos, window, ppq, loopStart = 0, loopEnd = 0, barLength = barBeats()) {
  const out = [], looping = loopEnd > loopStart;
  let cursor = pos, remaining = window, offset = 0;
  for (let guard = 0; remaining > 0 && guard < 16; guard++) {
    const limit = looping ? Math.min(loopEnd, cursor + remaining) : cursor + remaining;
    for (let beat = Math.ceil(cursor / ppq); beat * ppq < limit; beat++) out.push({ offset: offset + (beat * ppq - cursor), accent: beat % barLength === 0 });
    offset += limit - cursor; remaining -= limit - cursor;
    if (!looping || limit < loopEnd) break;
    cursor = loopStart;
  }
  return out;
}

// ---- view ----

/** The pitch range to centre on when opening: the notes' range, or C4 to C5 for an empty clip (decision 20.5). */
export function openCentre(rows) {
  if (!rows.length) return { low: 60, high: 72 };
  return { low: Math.min(...rows.map((row) => row.pitch)), high: Math.max(...rows.map((row) => row.pitch)) };
}

/** scrollY (px) that puts the middle of [low, high] in the middle of a view `viewH` tall, rows are 127 - pitch from the top. */
export function scrollYForCentre(low, high, rowH, viewH) {
  const middle = (127 - (low + high) / 2) * rowH + rowH / 2;
  return Math.max(0, Math.min(128 * rowH - viewH, Math.round(middle - viewH / 2)));
}

// ---- commit queue (decision 28) ----

/**
 * Serializes commits. `write(text)` resolves true when the project accepted the text. One gesture is one `enqueue`; the optimistic model update is the
 * caller's; a rejected commit calls `onRollback(previousText)`. A commit whose text equals the last committed text does nothing (no undo step, 20.13).
 */
export function createCommitQueue({ write, onRollback = () => {}, initial = '' }) {
  let tail = Promise.resolve(), committed = initial;
  return {
    enqueue(text) {
      tail = tail.then(async () => {
        if (text === committed) return 'unchanged';
        let ok = false;
        try { ok = await write(text); } catch { ok = false; }
        if (ok) { committed = text; return 'committed'; }
        onRollback(committed);
        return 'rolled-back';
      });
      return tail;
    },
    reset(text) { committed = text; },
    get committed() { return committed; },
  };
}

// ---- edge auto-scroll (decision 20.2 and 20.12) ----

/** Scroll speed in px per frame for a pointer `overshoot` px into the scroll zone (the last 16 px inside the view edge and everything beyond): 0 outside the zone, growing with the distance, capped. Driven by a frame timer, so holding the pointer there keeps scrolling. */
export function autoScrollSpeed(overshoot, max = 48) {
  if (!(overshoot > 0)) return 0;
  return Math.min(max, 1 + overshoot * 0.35);
}

/** Signed auto-scroll step (px per frame) for a pointer coordinate `pos` in a view `size` px long: negative near or past the start edge, positive near or past the end. */
export function autoScrollDelta(pos, size, zone = 16, max = 48) {
  if (pos < zone) return -autoScrollSpeed(zone - pos, max);
  if (pos > size - zone) return autoScrollSpeed(pos - (size - zone), max);
  return 0;
}
