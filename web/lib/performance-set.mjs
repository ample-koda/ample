// Performance-set readiness (issue #272): decides, per clip, whether it can keep playing without the server.
// Pure logic: the caller supplies what it has already resolved (live plan, decoded assets, rendered takes).

export const READY_LIVE = 'ready-live';
export const READY_PREPARED = 'ready-prepared';
export const UNAVAILABLE = 'unavailable';

/** Block types whose live graph needs the server at start (convolution fetches its impulse response). */
export const SERVER_BOUND = new Set(['convolution']);

/**
 * clip: { id, plan, assets, prepared }
 *   plan      result of planChain ({ ok, items, reason })
 *   assets    Set of asset keys already decoded in memory (source takes, impulses, worklets as 'worklet:<name>')
 *   prepared  null, or { key, bytes } for an explicitly chosen rendered audio take that is decoded in memory
 * Returns { id, state, reason, bytes }.
 */
export function classifyClip(clip) {
  const { id, plan, assets = new Set(), prepared = null } = clip;
  const fallback = (why) => (prepared
    ? { id, state: READY_PREPARED, reason: `${why}; plays as the prepared audio take`, bytes: prepared.bytes || 0, pins: [prepared.key] }
    : { id, state: UNAVAILABLE, reason: `${why}; no prepared audio take chosen`, bytes: 0, pins: [] });
  if (!plan || !plan.ok) return fallback(plan?.reason || 'the live chain cannot be planned');
  const missing = [];
  const pins = [];
  for (const item of plan.items) {
    if (item.kind === 'source') {
      const key = `chain:${item.take}${item.role ? `:${item.role}` : ''}`;
      if (assets.has(key)) pins.push(key); else missing.push(`source ${item.take}`);
    } else if (SERVER_BOUND.has(item.tpl)) {
      const key = `impulse:${item.id}`;
      if (assets.has(key)) pins.push(key); else missing.push(`impulse of ${item.id}`);
    }
  }
  if (missing.length) return fallback(`needs ${missing.join(', ')} from the server`);
  return { id, state: READY_LIVE, reason: 'all sources and impulses are in memory', bytes: 0, pins };
}

/**
 * Classifies every clip, then pins assets under a decoded-audio memory budget.
 * sizes: Map assetKey -> bytes. Clips are admitted in order; one that does not fit becomes unavailable.
 * Returns { clips, pinned: Set, bytes, budget, ok } where ok means every clip is ready.
 */
export function prepareSet(clips, { sizes = new Map(), budget = Infinity } = {}) {
  const pinned = new Set();
  let bytes = 0;
  const report = clips.map((clip) => {
    const result = classifyClip(clip);
    if (result.state === UNAVAILABLE) return result;
    const add = result.pins.filter((k) => !pinned.has(k)).reduce((sum, k) => sum + (sizes.get(k) || (k === clip.prepared?.key ? clip.prepared.bytes || 0 : 0)), 0);
    if (bytes + add > budget) return { ...result, state: UNAVAILABLE, reason: `not enough decoded-audio memory (needs ${add} more bytes, ${Math.max(0, budget - bytes)} left)`, pins: [] };
    result.pins.forEach((k) => pinned.add(k));
    bytes += add;
    return result;
  });
  return { clips: report, pinned, bytes, budget, ok: report.every((c) => c.state !== UNAVAILABLE) };
}

/** Assets a content change invalidates: any clip pinning one of the changed keys is no longer ready. */
export function invalidated(report, changedKeys) {
  const changed = new Set(changedKeys);
  return report.clips.filter((c) => c.pins.some((k) => changed.has(k))).map((c) => c.id);
}
