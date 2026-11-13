// Performance scenes (issue 273): queue a named scene as ONE launch batch on one downbeat.
// Pure logic, no DOM. The Performance workspace (issue 266) plugs in through the hook at the bottom.

/** Lane intents: start a clip, keep what the lane plays now, or stop the lane. */
export const INTENTS = ['start', 'keep', 'stop'];

/**
 * Resolves a scene against the running state into one launch batch.
 * scene: { id, name, lanes: [{ lane, intent, clip? }] }
 * state: { lanes: { [lane]: { playing: clipId|null } }, available: (clipId) => boolean }
 * Returns { ok:true, batch:[{lane, action, clip?}] } or { ok:false, problems:[{lane, clip, reason}] }
 * (on a problem nothing is launched, so the current scene keeps playing).
 */
export function resolveScene(scene, state) {
  const problems = [];
  const batch = [];
  for (const entry of scene?.lanes || []) {
    const { lane, intent, clip } = entry;
    if (!INTENTS.includes(intent)) { problems.push({ lane, clip, reason: `unknown intent "${intent}"` }); continue; }
    if (intent === 'keep') { batch.push({ lane, action: 'keep' }); continue; }
    if (intent === 'stop') { batch.push({ lane, action: 'stop' }); continue; }
    if (!clip || !state.available(clip)) { problems.push({ lane, clip, reason: 'clip is not available' }); continue; }
    batch.push({ lane, action: 'start', clip });
  }
  return problems.length ? { ok: false, problems } : { ok: true, batch };
}

/**
 * Queue of at most one pending scene.
 * deps: { now(): seconds, nextDownbeat(): seconds, launch(at, batch): void, leadSeconds?: number }
 * The launch happens once, with one transport timestamp for all lanes. cancel() works until at - leadSeconds
 * (the scheduling deadline); after that the batch is already handed to the transport.
 */
export function createSceneQueue(deps) {
  const lead = deps.leadSeconds ?? 0.1;
  let pending = null;
  const listeners = new Set();
  const emit = () => listeners.forEach(fn => fn(pending && { scene: pending.scene, at: pending.at }));

  return {
    onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    /** The pending scene ({scene, at}) or null, for showing it. */
    get pending() { return pending && { scene: pending.scene, at: pending.at }; },
    /** Queues a scene; replaces a pending one. Returns the resolve result (with `at` when ok). */
    queue(scene, state) {
      const r = resolveScene(scene, state);
      if (!r.ok) return r;
      const at = deps.nextDownbeat();
      pending = { scene, at, batch: r.batch, committed: false };
      emit();
      return { ...r, at };
    },
    cancel() {
      if (!pending || deps.now() >= pending.at - lead) return false;
      pending = null; emit(); return true;
    },
    /** Call from the scheduler tick: hands the batch to the transport once the deadline is reached. */
    tick() {
      if (!pending || pending.committed || deps.now() < pending.at - lead) return false;
      pending.committed = true;
      deps.launch(pending.at, pending.batch);
      return true;
    },
    /** Call when the downbeat has passed to clear the pending marker. */
    settle() { if (pending?.committed && deps.now() >= pending.at) { pending = null; emit(); } },
  };
}

// HOOK (issue 266): the Performance workspace imports createSceneQueue/resolveScene from here and wires
// a scene button to queue(), a "pending" badge to onChange(), and a cancel button to cancel().
