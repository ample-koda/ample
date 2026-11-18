// Live Preview in the code editor (plan/live-preview/PLAN.md, WP1): the listening session as a small controller with its dependencies passed in, so it is
// tested in node. It decides when to build (after a typing pause, at once, or by flushing the waiting pause), never runs two builds at the same time (a change
// during a build builds once more afterwards, with the newest state, like web/lib/live-swap.mjs), keeps the last good build playing when a build fails, and
// on Stop cancels the waiting pause and the running build and makes sure a build that still finishes is not played.

/**
 * @param {object} deps
 * @param {(job: object, signal: { cancelled: boolean }) => Promise<object|null>} deps.build builds the job; resolves with what to play, null when it was cancelled; throws on a failed build
 * @param {() => void} deps.cancelBuild cancels the running build (the store's stopBuild)
 * @param {(result: object, number: number, signal: { cancelled: boolean }) => Promise<void>} deps.play hands a finished build to the player (which swaps
 *   at the loop boundary or at once); it must stop and change nothing when `signal.cancelled` is set (Stop or a new Listen came in while it loaded)
 * @param {() => void} deps.stopPlayback stops the sound
 * @param {(state: object) => void} [deps.onChange] told after every change of state
 * @param {(fn: () => void, ms: number) => any} [deps.setTimer]
 * @param {(id: any) => void} [deps.clearTimer]
 * @param {number} [deps.pause] the typing pause in ms before a change builds (default 600)
 */
export function createListenSession(deps) {
  const setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
  const clearTimer = deps.clearTimer ?? ((id) => clearTimeout(id));
  const S = {
    active: false, status: 'idle', job: null, timer: null, running: false, again: false, generation: 0, next: 1, building: null, playing: null, error: null,
    lastResult: null, token: { cancelled: false },
  };
  // Stop and a new Listen end the session of the old token: its tasks see `cancelled` after every await and change nothing any more
  const endSession = () => { S.token.cancelled = true; S.token = { cancelled: false }; };
  const emit = () => deps.onChange?.(state());
  let ready = null;   // a build that is made already, for the next run

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
    const generation = S.generation, token = S.token;
    try {
      do {
        S.again = false;
        const number = S.next++, job = S.job, signal = token;
        S.building = number; S.status = 'building'; S.error = null; emit();
        let result = null;
        try {
          if (ready) { result = ready; ready = null; } else result = await deps.build(job, signal);   // a build that was made before (Ctrl+Enter) plays without a new one
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
        try {
          await deps.play(result, number, token);
        } catch (error) {
          if (generation !== S.generation || !S.active) return;   // an old session's late failure is not the new session's
          S.error = error?.message || String(error); S.status = 'error'; emit();   // like a failed build: the last good build keeps playing
          continue;
        }

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
    /** Starts listening to a job. `readyResult` is a build of this job that is already made (the caller has checked that it is still good): it plays at once. */
    start(job, readyResult = null) {
      endSession();
      S.generation++; S.running = false; S.again = false;
      S.active = true; S.job = job; S.playing = null; S.error = null; S.status = 'idle'; ready = readyResult;
      return run();
    },
    /** The target changed in a way that needs no new build (another range of the same render): only the job that the next build and the status use. */
    retarget(job) { S.job = job; emit(); },
    /** The sound of build `number` plays now (a range that did not fit it before fits now): its error is over. */
    recover(number) { if (!S.active) return; S.error = null; S.playing = number; if (S.status === 'error') S.status = 'playing'; emit(); },
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
      endSession();
      S.generation++; S.active = false; S.running = false; S.again = false; S.building = null;
      if (wasRunning) deps.cancelBuild();
      deps.stopPlayback();
      S.status = 'stopped'; S.playing = null;
      emit();
    },
  };
}
