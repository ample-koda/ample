// Performance scenes (issue 273): queue a named scene as ONE launch batch on one downbeat.
// Pure logic, no DOM, nothing runs at import time. The Performance workspace (issue 266) plugs in through the
// hook at the bottom.
//
// Units: times are seconds on the one transport clock (the AudioContext clock of the launch engine, issue 266);
// lanes are named by string and clips by their id inside the lane (the engine's addClip ids).

/** Lane intents: start a clip, keep what the lane plays now, or stop the lane. */
export const INTENTS = ['start', 'keep', 'stop'];

/**
 * Resolves a scene against the running state into one launch batch.
 * scene: { id, name, lanes: [{ lane, intent, clip? }] }
 * state: { available: (clipId, lane) => boolean }   (`lanes` of the state are not needed to resolve)
 * Returns { ok:true, batch:[{lane, action, clip?}] } or { ok:false, problems:[{lane, clip, reason}] }
 * (on a problem nothing is launched, so the current scene keeps playing). A lane may appear once.
 */
export function resolveScene(scene, state) {
  if (typeof state?.available !== 'function') throw new TypeError('resolveScene needs state.available(clipId, lane)');
  const problems = [];
  const batch = [];
  const seen = new Set();
  for (const entry of scene?.lanes || []) {
    const { lane, intent, clip } = entry;
    if (seen.has(lane)) { problems.push({ lane, clip, reason: 'lane appears twice in the scene' }); continue; }
    seen.add(lane);
    if (!INTENTS.includes(intent)) { problems.push({ lane, clip, reason: `unknown intent "${intent}"` }); continue; }
    if (intent === 'keep') { batch.push({ lane, action: 'keep' }); continue; }
    if (intent === 'stop') { batch.push({ lane, action: 'stop' }); continue; }
    if (clip === undefined || clip === null || !state.available(clip, lane)) {
      problems.push({ lane, clip, reason: 'clip is not available' });
      continue;
    }
    batch.push({ lane, action: 'start', clip });
  }
  return problems.length ? { ok: false, problems } : { ok: true, batch };
}

const copyBatch = (batch) => batch.map((b) => ({ ...b }));
const copyScene = (scene) => ({ ...scene, lanes: (scene?.lanes || []).map((e) => ({ ...e })) });

/**
 * Queue of at most one pending scene.
 * deps: { now(): seconds, nextDownbeat(): seconds, launch(at, batch): void, leadSeconds?: number }
 * The launch happens once, with one transport timestamp for all lanes. cancel() works until at - leadSeconds
 * (the scheduling deadline); after that the batch is already handed to the transport. Everything handed out
 * (pending, results, listener arguments, the batch given to launch) is a copy.
 */
export function createSceneQueue(deps) {
  const lead = deps.leadSeconds ?? 0.1;
  if (!Number.isFinite(lead) || lead < 0) throw new RangeError('leadSeconds must be a number of seconds >= 0');
  let pending = null;
  const listeners = new Set();
  const view = () => pending && { scene: copyScene(pending.scene), at: pending.at };
  const emit = () => {
    for (const fn of [...listeners]) {
      try { fn(view()); } catch { /* a broken listener must not break the queue */ }
    }
  };
  const time = () => deps.now();
  const settle = () => { if (pending?.committed && time() >= pending.at) { pending = null; emit(); } };

  return {
    onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
    /** The pending scene ({scene, at}) or null, for showing it. */
    get pending() { return view(); },
    /**
     * Queues a scene; replaces a pending one that is not yet handed to the transport. Returns the resolve result
     * (with `at` when ok). A scene already handed to the transport (inside the deadline) cannot be replaced.
     */
    queue(scene, state) {
      settle();
      if (pending?.committed) {
        const reason = 'a scene is already scheduled on the next downbeat';
        return { ok: false, problems: [{ lane: null, clip: null, reason }] };
      }
      const r = resolveScene(scene, state);
      if (!r.ok) return r;
      const at = deps.nextDownbeat();
      if (!Number.isFinite(at) || !(at >= time())) {
        return { ok: false, problems: [{ lane: null, clip: null, reason: `no valid downbeat (${at})` }] };
      }
      pending = { scene: copyScene(scene), at, batch: copyBatch(r.batch), committed: false };
      emit();
      return { ok: true, batch: copyBatch(r.batch), at };
    },
    cancel() {
      const t = time();
      if (!pending || pending.committed || !Number.isFinite(t) || t >= pending.at - lead) return false;
      pending = null; emit(); return true;
    },
    /** Drops the pending scene whatever the deadline (Stop all). What the transport already holds is the transport's to clear. */
    clear() {
      if (!pending) return false;
      pending = null; emit(); return true;
    },
    /** Call from the scheduler tick: hands the batch to the transport once the deadline is reached. */
    tick() {
      const t = time();
      if (!pending || pending.committed || !Number.isFinite(t) || t < pending.at - lead) return false;
      pending.committed = true;
      try { deps.launch(pending.at, copyBatch(pending.batch)); } catch (e) { pending = null; emit(); throw e; }
      return true;
    },
    /** Call when the downbeat has passed to clear the pending marker. */
    settle,
  };
}

/**
 * Wires a scene queue to the launch engine of issue 266 (createPerformance) and to readiness (issue 272).
 * engine: { now, nextBar, launchBatch(at, batch), stopAll, state() }
 * available(clipId, lane) => boolean, e.g. (id) => isAvailable(report, id) from performance-set.mjs.
 * Returns { queue(scene), cancel, tick, settle, stopAll, pending, onChange }. stopAll() also drops the pending scene.
 */
export function createEngineSceneQueue(engine, { available, leadSeconds } = {}) {
  const q = createSceneQueue({
    now: engine.now, nextDownbeat: engine.nextBar, launch: (at, batch) => engine.launchBatch(at, batch), leadSeconds,
  });
  const running = () => {
    const lanes = {};
    for (const [name, clips] of Object.entries(engine.state())) {
      lanes[name] = { playing: clips.find((c) => c.status === 'playing')?.id ?? null };
    }
    return { lanes, available };
  };
  return {
    queue: (scene) => q.queue(scene, running()),
    cancel: () => q.cancel(),
    tick: () => q.tick(),
    settle: () => q.settle(),
    stopAll() { q.clear(); engine.stopAll(); },
    get pending() { return q.pending; },
    onChange: (fn) => q.onChange(fn),
  };
}

// HOOK (issue 266): the Performance workspace imports createSceneQueue/resolveScene from here and wires
// a scene button to queue(), a "pending" badge to onChange(), and a cancel button to cancel().
