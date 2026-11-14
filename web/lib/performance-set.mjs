// Performance-set readiness (issue #272): decides, per clip, whether it can keep playing without the server.
// Pure logic, nothing runs at import time: the caller supplies what it has already resolved (live plan,
// decoded assets, rendered takes). Reports are immutable snapshots: every change returns a new report.

export const READY_LIVE = 'ready-live';
export const READY_PREPARED = 'ready-prepared';
export const UNAVAILABLE = 'unavailable';

/** Block types whose live graph needs the server at start (convolution fetches its impulse response). */
export const SERVER_BOUND = new Set(['convolution']);

const validBytes = (n) => Number.isFinite(n) && n >= 0;

/**
 * clip: { id, plan, assets, prepared }
 *   id        the id the launch engine and the scenes use for this clip (unique in a set)
 *   plan      result of planChain ({ ok, items, reason })
 *   assets    Set (or array) of asset keys already decoded in memory (source takes, impulses, worklets as 'worklet:<name>')
 *   prepared  null, or { key, bytes } for an explicitly chosen rendered audio take that is decoded in memory
 * Returns { id, state, reason, bytes, pins }: bytes is filled in by prepareSet (the decoded size of the pins).
 */
export function classifyClip(clip) {
  const { id, plan, prepared = null } = clip;
  const assets = clip.assets instanceof Set ? clip.assets : new Set(clip.assets || []);
  const fallback = (why) => (prepared
    ? {
      id,
      state: READY_PREPARED,
      reason: `${why}; plays as the prepared audio take`,
      bytes: validBytes(prepared.bytes) ? prepared.bytes : 0,
      pins: [prepared.key],
    }
    : { id, state: UNAVAILABLE, reason: `${why}; no prepared audio take chosen`, bytes: 0, pins: [] });
  if (!plan || !plan.ok) return fallback(plan?.reason || 'the live chain cannot be planned');
  const missing = [];
  const pins = new Set();
  for (const item of plan.items || []) {
    if (item.kind === 'source') {
      const key = `chain:${item.take}${item.role ? `:${item.role}` : ''}`;
      if (assets.has(key)) pins.add(key); else missing.push(`source ${item.take}`);
    } else if (SERVER_BOUND.has(item.tpl)) {
      const key = `impulse:${item.id}`;
      if (assets.has(key)) pins.add(key); else missing.push(`impulse of ${item.id}`);
    }
  }
  if (missing.length) return fallback(`needs ${missing.join(', ')} from the server`);
  return { id, state: READY_LIVE, reason: 'all sources and impulses are in memory', bytes: 0, pins: [...pins] };
}

const freezeClip = (c) => Object.freeze({ ...c, pins: Object.freeze([...c.pins]) });
const freezeReport = (clips, pinSizes, budget) => Object.freeze({
  clips: Object.freeze(clips.map(freezeClip)), pinned: new Set(pinSizes.keys()), pinSizes: new Map(pinSizes),
  bytes: [...pinSizes.values()].reduce((a, b) => a + b, 0), budget, ok: clips.every((c) => c.state !== UNAVAILABLE),
});

/**
 * Classifies every clip, then pins assets under a decoded-audio memory budget.
 * sizes: Map assetKey -> bytes (the prepared take may give its own `bytes`). budget: bytes, Infinity for none.
 * Clips are admitted in the order given (that order is the priority); a clip whose assets do not fit in what is
 * left, or whose size is unknown under a finite budget, becomes unavailable; a clip larger than the whole budget
 * can never fit. An asset shared by several clips is counted once. Nothing is evicted: pins are only those of
 * admitted clips, and everything else is free to be dropped by the caller.
 * Returns { clips, pinned: Set (a copy), bytes, budget, ok } where ok means every clip is ready.
 */
export function prepareSet(clips, { sizes = new Map(), budget = Infinity } = {}) {
  if (typeof budget !== 'number' || Number.isNaN(budget) || budget < 0) {
    throw new RangeError(`budget must be a number of bytes >= 0 (or Infinity), got ${budget}`);
  }
  const ids = new Set();
  for (const c of clips) { if (ids.has(c.id)) throw new Error(`duplicate clip id ${c.id}`); ids.add(c.id); }
  const pinned = new Map(); // asset key -> bytes
  let bytes = 0;
  const report = clips.map((clip) => {
    const result = classifyClip(clip);
    if (result.state === UNAVAILABLE) return result;
    const sizeOf = (k) => {
      const s = sizes.get(k);
      if (validBytes(s)) return s;
      return k === clip.prepared?.key && validBytes(clip.prepared.bytes) ? clip.prepared.bytes : undefined;
    };
    const unknown = result.pins.filter((k) => sizeOf(k) === undefined);
    if (unknown.length && budget !== Infinity) {
      const reason = `size of ${unknown.join(', ')} is unknown, so the memory budget cannot be kept`;
      return { ...result, state: UNAVAILABLE, reason, bytes: 0, pins: [] };
    }
    const size = (k) => sizeOf(k) ?? 0;
    const add = result.pins.filter((k) => !pinned.has(k)).reduce((sum, k) => sum + size(k), 0);
    if (bytes + add > budget) {
      const reason = `not enough decoded-audio memory (needs ${add} more bytes, ${Math.max(0, budget - bytes)} left)`;
      return { ...result, state: UNAVAILABLE, reason, bytes: 0, pins: [] };
    }
    result.pins.forEach((k) => pinned.set(k, size(k)));
    bytes += add;
    return { ...result, bytes: result.pins.reduce((sum, k) => sum + size(k), 0) };
  });
  return freezeReport(report, pinned, budget);
}

/** Assets a content change invalidates: any clip pinning one of the changed keys is no longer ready. */
export function invalidated(report, changedKeys) {
  const changed = new Set(changedKeys);
  return report.clips.filter((c) => c.pins.some((k) => changed.has(k))).map((c) => c.id);
}

/**
 * The report after a content change: the affected clips become unavailable ("changed, prepare again"), their
 * pins are released (unless another clip still pins the asset) and the byte total shrinks. The old report is untouched.
 */
export function invalidate(report, changedKeys) {
  const hit = new Set(invalidated(report, changedKeys));
  if (!hit.size) return report;
  const changed = { state: UNAVAILABLE, reason: 'content changed; prepare the set again', bytes: 0, pins: [] };
  const clips = report.clips.map((c) => (hit.has(c.id) ? { ...c, ...changed } : c));
  const stillPinned = new Set(clips.flatMap((c) => c.pins));
  const pinSizes = new Map([...report.pinSizes].filter(([k]) => stillPinned.has(k)));
  return freezeReport(clips, pinSizes, report.budget);
}

/** Whether a clip id can be launched now (ready live or prepared). Unknown ids are not available. */
export function isAvailable(report, id) {
  const c = report?.clips?.find((x) => x.id === id);
  return !!c && c.state !== UNAVAILABLE;
}
