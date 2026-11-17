// Live Preview in the code editor (plan/live-preview/PLAN.md, WP1): the listening session as a small controller with its dependencies passed in, so it is
// tested in node. It decides when to build (after a typing pause, at once, or by flushing the waiting pause), never runs two builds at the same time (a change
// during a build builds once more afterwards, with the newest state, like web/lib/live-swap.mjs), keeps the last good build playing when a build fails, and
// on Stop cancels the waiting pause and the running build and makes sure a build that still finishes is not played.

/**
 * @param {object} deps
 * @param {(job: object, signal: { cancelled: boolean }) => Promise<object|null>} deps.build builds the job; resolves with what to play, null when it was cancelled; throws on a failed build
 * @param {() => void} deps.cancelBuild cancels the running build (the store's stopBuild)
 * @param {(result: object, number: number) => Promise<void>} deps.play hands a finished build to the player (which swaps at the loop boundary or at once)
 * @param {() => void} deps.stopPlayback stops the sound
 * @param {(state: object) => void} [deps.onChange] told after every change of state
 * @param {(fn: () => void, ms: number) => any} [deps.setTimer]
 * @param {(id: any) => void} [deps.clearTimer]
 * @param {number} [deps.pause] the typing pause in ms before a change builds (default 600)
 */
export function createListenSession(deps) {
  const setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
  const clearTimer = deps.clearTimer ?? ((id) => clearTimeout(id));
  const S = { active: false, status: 'idle', job: null, timer: null, running: false, again: false, generation: 0, next: 1, building: null, playing: null, error: null, lastResult: null };
  const emit = () => deps.onChange?.(state());

  function state() {
    return {
      active: S.active, status: S.status, building: S.building, playing: S.playing, error: S.error,
      pending: S.timer !== null, job: S.job, result: S.lastResult,
    };
  }

  async function run() {
    if (S.timer !== null) { clearTimer(S.timer); S.timer = null; }
    if (!S.active || !S.job) return;
    if (S.running) { S.again = true; emit(); return; }
    S.running = true;
    const generation = S.generation;
    try {
      do {
        S.again = false;
        const number = S.next++, job = S.job, signal = { cancelled: false };
        S.building = number; S.status = 'building'; S.error = null; emit();
        let result = null;
        try {
          result = await deps.build(job, signal);
        } catch (error) {
          if (generation !== S.generation) return;
          S.error = error?.message || String(error); S.building = null;
          S.status = 'error';   // the last good build (if any) keeps playing; the state (playing) says which
          emit();
          continue;
        }
        if (generation !== S.generation || !S.active) return;   // stopped (or restarted) meanwhile: a late build is not played
        S.building = null;
        if (!result) { S.status = S.playing !== null ? 'playing' : 'idle'; emit(); continue; }   // cancelled by the store
        if (S.again) continue;   // a newer change came in: build that one before anything is heard
        S.lastResult = result;
        S.status = S.playing !== null ? 'playing' : 'idle'; emit();   // the build is done: the player loads the new take now (its own state says so)
        await deps.play(result, number);
        if (generation !== S.generation || !S.active) return;
        S.playing = number; S.status = 'playing'; emit();
      } while (S.again && S.active && generation === S.generation);
    } finally {
      if (generation === S.generation) S.running = false;
    }
  }

  return {
    state,
    /** Starts listening to a job (a target with its settings): builds at once and plays the result. */
    start(job) {
      S.generation++; S.running = false; S.again = false;
      S.active = true; S.job = job; S.playing = null; S.error = null; S.status = 'idle';
      return run();
    },
    /** The target or its settings changed: a new build after the pause (or at once when `now`). */
    setJob(job, now = false) {
      S.job = job;
      if (!S.active) return Promise.resolve();
      return now ? run() : (this.changed(), Promise.resolve());
    },
    /** Something that the target depends on changed (the text was applied): build after the typing pause. */
    changed() {
      if (!S.active) return;
      if (S.timer !== null) clearTimer(S.timer);
      S.timer = setTimer(() => { S.timer = null; run(); }, deps.pause ?? 600);
      emit();
    },
    /** Ctrl+Enter: build now (during a build: once more afterwards, never two at the same time). */
    buildNow() { return run(); },
    /** Ctrl+Shift+Enter: the waiting pause is over at once; nothing happens when no change waits. */
    flush() { if (S.timer === null) return Promise.resolve(); return run(); },
    /** Stop: no waiting pause, the running build is cancelled, the sound stops, and a build that still finishes is not played. */
    stop() {
      if (S.timer !== null) { clearTimer(S.timer); S.timer = null; }
      const wasRunning = S.running;
      S.generation++; S.active = false; S.running = false; S.again = false; S.building = null;
      if (wasRunning) deps.cancelBuild();
      deps.stopPlayback();
      S.status = 'stopped'; S.playing = null;
      emit();
    },
  };
}
