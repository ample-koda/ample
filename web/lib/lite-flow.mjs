// Pure view and flow helpers of the Lite roll "AMPLE Jam" (plan MLS12): pointer-anchored zoom, zoom keys, centring on the notes, the paste
// position rule, the per-block view memory (serialize / restore, tolerant of anything that was stored) and the default view of a clip. No DOM. @ample:pianoroll-lite

export const LIMITS = { minPxBeat: 6, maxPxBeat: 480, minRowH: 6, maxRowH: 40, minGridRowH: 14, maxGridRowH: 56, gridRowH: 30 };
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/**
 * Zooms around the pointer so that the beat and the row under the pointer stay under it. `view` is { pxBeat, rowH, scrollX, scrollY }, `pointer` { x, y } in
 * grid pixels, `factor` the multiplier; `axis` is 'time', 'rows' or 'both'. `max` ({ x, y }) caps the scroll when known; the scroll never goes below 0.
 */
export function zoomAnchor(view, pointer, factor, axis = 'time', max = {}) {
  const next = { ...view };
  if (axis === 'time' || axis === 'both') {
    const beat = (pointer.x + view.scrollX) / view.pxBeat;
    next.pxBeat = clamp(view.pxBeat * factor, LIMITS.minPxBeat, LIMITS.maxPxBeat);
    next.scrollX = clamp(beat * next.pxBeat - pointer.x, 0, max.x ?? Infinity);
  }
  if (axis === 'rows' || axis === 'both') {
    const row = (pointer.y + view.scrollY) / view.rowH;
    next.rowH = clamp(view.rowH * factor, LIMITS.minRowH, LIMITS.maxRowH);
    next.scrollY = clamp(row * next.rowH - pointer.y, 0, max.y ?? Infinity);
  }
  return next;
}

/** The scrollY (px) that centres the pitch range of the notes (C4-C5 for an empty clip) in a view `viewH` tall; rows run 127 - pitch from the top. */
export function centerOnNotes(rows, rowH, viewH) {
  const low = rows.length ? Math.min(...rows.map((r) => r.pitch)) : 60, high = rows.length ? Math.max(...rows.map((r) => r.pitch)) : 72;
  const middle = (127 - (low + high) / 2) * rowH + rowH / 2;
  return clamp(Math.round(middle - viewH / 2), 0, Math.max(0, 128 * rowH - viewH));
}

/** Shift+1..3: three fixed time zoom levels (px per beat). */
export const ZOOM_PRESETS = [24, 60, 140];
export const zoomPreset = (n) => ZOOM_PRESETS[clamp(Math.round(n), 1, 3) - 1];

/** The time view that shows [from, to] ticks across `viewW` px with a small margin (Shift+4 = the whole clip, Shift+5 = the selection). */
export function fitRange(from, to, ppq, viewW, margin = 24) {
  const beats = Math.max(0.25, (to - from) / ppq), pxBeat = clamp((viewW - 2 * margin) / beats, LIMITS.minPxBeat, LIMITS.maxPxBeat);
  return { pxBeat, scrollX: Math.max(0, (from / ppq) * pxBeat - margin) };
}

/**
 * Where Ctrl+V lands (decision 17, option C): the snapped pointer position while the pointer is over the grid, else the playhead. `tickAtPointer` is null when the
 * pointer is not over the grid.
 */
export function pasteTick(tickAtPointer, playhead, snap = (t) => t) {
  return Math.max(0, snap(tickAtPointer === null || tickAtPointer === undefined ? playhead : tickAtPointer));
}

// ---- view memory per block ----

export const VIEW_VERSION = 1;

/** The Lite state as a plain JSON-safe record (what is remembered per block). */
/** The names given to Grid rows: a pitch (0-127) to a short text. */
export function cleanNames(raw) {
  const out = {};
  for (const [key, value] of Object.entries(raw || {})) { const pitch = Number(key), text = String(value ?? '').trim().slice(0, 24); if (Number.isInteger(pitch) && pitch >= 0 && pitch <= 127 && text) out[pitch] = text; }
  return out;
}

export function serializeView(s) {
  return {
    v: VIEW_VERSION,
    panel: s.panel ? { left: Math.round(s.panel.left), top: Math.round(s.panel.top), width: Math.round(s.panel.width), height: Math.round(s.panel.height) } : null,
    bracket: s.bracket ? { start: Math.round(s.bracket.start), end: Math.round(s.bracket.end) } : null,
    loopOn: !!s.loopOn, follow: s.follow !== false, snap: String(s.snap ?? 'auto'),
    pxBeat: Math.round(s.pxBeat * 100) / 100, rowH: Math.round(s.rowH * 100) / 100, scrollX: Math.round(s.scrollX), scrollY: Math.round(s.scrollY),
    lastLen: Math.max(0, Math.round(s.lastLen || 0)), hintDismissed: !!s.hintDismissed, mode: s.mode === 'grid' ? 'grid' : 'roll',
    ...(Number.isFinite(s.gridRowH) && Math.round(s.gridRowH * 100) / 100 !== LIMITS.gridRowH ? { gridRowH: Math.round(clamp(s.gridRowH, LIMITS.minGridRowH, LIMITS.maxGridRowH) * 100) / 100 } : {}),
    ...(s.names && typeof s.names === 'object' && Object.keys(cleanNames(s.names)).length ? { names: cleanNames(s.names) } : {}),
    ...(Array.isArray(s.muted) && s.muted.length ? { muted: [...new Set(s.muted.filter((p) => Number.isInteger(p) && p >= 0 && p <= 127))].sort((a, b) => a - b) } : {}),
  };
}

/**
 * Reads a remembered record back. Anything wrong (not an object, wrong version, NaN, out of range) is dropped field by field; a bracket must satisfy
 * 0 <= start < end <= endTick; `snaps` lists the valid snap ids. Returns an object with only the valid fields (possibly empty), never throws.
 */
export function restoreView(raw, { endTick = Infinity, snaps = [] } = {}) {
  const out = {};
  if (!raw || typeof raw !== 'object' || raw.v !== VIEW_VERSION) return out;
  const p = raw.panel;
  if (p && [p.left, p.top, p.width, p.height].every(Number.isFinite) && p.width >= 320 && p.height >= 240) out.panel = { left: Math.max(0, p.left), top: Math.max(0, p.top), width: p.width, height: p.height };
  const b = raw.bracket;
  if (b && Number.isFinite(b.start) && Number.isFinite(b.end) && b.start >= 0 && b.end > b.start && b.end <= endTick) out.bracket = { start: b.start, end: b.end };
  if (typeof raw.loopOn === 'boolean') out.loopOn = raw.loopOn;
  if (typeof raw.follow === 'boolean') out.follow = raw.follow;
  if (typeof raw.snap === 'string' && snaps.includes(raw.snap)) out.snap = raw.snap;
  if (Number.isFinite(raw.pxBeat)) out.pxBeat = clamp(raw.pxBeat, LIMITS.minPxBeat, LIMITS.maxPxBeat);
  if (Number.isFinite(raw.rowH)) out.rowH = clamp(raw.rowH, LIMITS.minRowH, LIMITS.maxRowH);
  if (Number.isFinite(raw.scrollX)) out.scrollX = Math.max(0, raw.scrollX);
  if (Number.isFinite(raw.scrollY)) out.scrollY = Math.max(0, raw.scrollY);
  if (Number.isFinite(raw.lastLen) && raw.lastLen > 0) out.lastLen = raw.lastLen;
  if (typeof raw.hintDismissed === 'boolean') out.hintDismissed = raw.hintDismissed;
  if (raw.mode === 'roll' || raw.mode === 'grid') out.mode = raw.mode;
  if (Number.isFinite(raw.gridRowH)) out.gridRowH = clamp(raw.gridRowH, LIMITS.minGridRowH, LIMITS.maxGridRowH);
  { const names = cleanNames(raw.names); if (Object.keys(names).length) out.names = names; }
  if (Array.isArray(raw.muted)) { const m = [...new Set(raw.muted.filter((p) => Number.isInteger(p) && p >= 0 && p <= 127))]; if (m.length) out.muted = m; }
  return out;
}

// ---- which view a clip opens in (decision 3) ----

/** Per-project store text -> { blockId: { view: 'lite'|'advanced', lite: {...} } }; tolerant of garbage. */
export function readViews(text) {
  try {
    const data = JSON.parse(text || '{}');
    if (!data || typeof data !== 'object' || Array.isArray(data)) return {};
    const out = {};
    for (const [id, rec] of Object.entries(data)) if (rec && typeof rec === 'object') out[id] = { ...(rec.view === 'lite' || rec.view === 'advanced' ? { view: rec.view } : {}), ...(rec.lite && typeof rec.lite === 'object' ? { lite: rec.lite } : {}) };
    return out;
  } catch { return {}; }
}

/** The record of one block changed by `patch` ({ view } and/or { lite }); returns the new store text. */
export function writeViews(text, blockId, patch) {
  const all = readViews(text);
  all[blockId] = { ...all[blockId], ...patch };
  return JSON.stringify(all);
}

/** The view a clip opens in: the remembered one, else Lite (decision 3, option A: the friendly front door). */
export const defaultView = (record) => (record?.view === 'advanced' ? 'advanced' : 'lite');

/** Selection continuity between the two editors: rows as stable `at:pitch` keys, and back to the indexes of another editor's rows. */
export const selectionKeys = (rows) => rows.map((r) => `${r.at}:${r.pitch}`);
export const keysToIndexes = (rows, keys) => { const set = new Set(keys); return rows.filter((r) => set.has(`${r.at}:${r.pitch}`)).map((r) => r.index); };
