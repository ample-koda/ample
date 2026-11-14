// Ordering rules of the live chain, kept free of the audio and DOM imports so they can be tested alone.

/** Starts a new render of block `id`: every older result for it is void from now on (bumped when scheduled, not when the work starts). */
export function bumpRevision(revs, id) {
  const rev = (revs.get(id) || 0) + 1;
  revs.set(id, rev);
  return rev;
}

/**
 * One scheduled re-render of block `id` and the local blocks that work on it, in plan order.
 * - the block itself keeps the revision it got when scheduled; blocks downstream get theirs when the job reaches them (their input is only known then)
 * - a result is adopted only while its revision is the newest of its block; a superseded block is left to the newer job, and so are the blocks downstream of it
 * - a failure of the newest render calls `invalidate(item, key)` (so the caller does not treat the block as up to date and retries on a change) and the independent blocks go on;
 *   blocks downstream of the failed one are not rendered from a sound that was not made
 * `keyOf(item)` is taken before the render, so the result is stored under what it was made for.
 */
export async function runRenderJob({ items, id, ticket, revs, alive, keyOf, render, adopt, invalidate, report }) {
  const dirty = new Set(), blocked = new Set();
  for (const item of items) {
    if (!(item.id === id || item.inputs.some((link) => dirty.has(link.from)))) continue;
    dirty.add(item.id);
    if (item.inputs.some((link) => blocked.has(link.from))) { blocked.add(item.id); continue; }
    const rev = item.id === id ? ticket : bumpRevision(revs, item.id), key = keyOf(item);
    try {
      const audio = await render(item);
      if (!alive() || revs.get(item.id) !== rev) { blocked.add(item.id); continue; }   // a newer render of this block started: the older result must not replace it
      adopt(item, audio, key);
    } catch (error) {
      if (!alive()) return;
      blocked.add(item.id);
      if (revs.get(item.id) === rev) { invalidate(item, key); report?.(error); }   // only the newest render's failure counts; an older one is moot
    }
  }
}

/** The A/B comparison source: a switch on supersedes a pending one (new intent, fresh load), a switch off cancels it; a load only clears the pending flag when it is still the newest. */
export function createCompareGate() {
  let intent = 0, pending = false;
  return {
    get pending() { return pending; },
    begin() { pending = true; return ++intent; },
    cancel() { const was = pending; intent++; pending = false; return was; },
    current(mine) { return mine === intent; },
    finish(mine) { if (mine === intent) pending = false; },
  };
}
