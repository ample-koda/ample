// Live Preview in the code editor: the cancellation of a listening session, carried to the points where it matters (pure, tested in node).
// A session hands every task a token { cancelled }; Stop (and a new Listen) cancels the token of the old session. A task that finishes after that must not
// build, must not hand audio to the player and must not change any state: it checks the token after every await that can outlast a Stop.

/** A task of the cancelled session reads `signal.cancelled`; an absent signal never cancels. */
export const alive = (signal) => !(signal && signal.cancelled);

/**
 * Waits until the store has no build of its own running (it runs one at a time), polling every `delay` ms. Resolves true when the store is free,
 * false when the task was cancelled meanwhile: the waiting task is withdrawn and never starts a build.
 */
export async function waitForFreeStore(isBusy, signal, { sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)), delay = 120 } = {}) {
  while (isBusy()) {
    if (!alive(signal)) return false;
    await sleep(delay);
  }
  return alive(signal);
}

/**
 * The builds of the session on top of the store. `build` waits for a build that is not ours, then for `beforeBuild` (a pause that is not a build, for example a
 * distance between two builds; it says false when the task was cancelled), and then builds. `cancel` (the session's cancelBuild) stops the store's build only
 * while the build runs that this builder started: a Stop during a wait leaves every other build running.
 * @param {{ isBusy: () => boolean, buildNow: (job: object, signal: object) => Promise<any>, stopStore: () => void,
 *   beforeBuild?: (job: object, signal: object) => Promise<boolean>, sleep?: Function, delay?: number }} deps
 */
export function createListenBuilder({ isBusy, buildNow, stopStore, beforeBuild, sleep, delay }) {
  let own = 0;
  return {
    async build(job, signal) {
      for (;;) {
        if (!(await waitForFreeStore(isBusy, signal, { sleep, delay }))) return null;
        if (beforeBuild && !(await beforeBuild(job, signal))) return null;
        if (!isBusy()) break;   // a build of the page started during the pause: wait for it too
      }
      own++;
      try { return await buildNow(job, signal); } finally { own--; }
    },
    cancel() { if (own > 0) stopStore(); },
  };
}

/**
 * Resumes a suspended audio context. Resolves true when the task may go on, false when it was cancelled before or during the resume: the caller then
 * leaves the player alone (a stale task must not queue its entry in a newer session).
 */
export async function resumeUnlessCancelled(context, signal) {
  if (!alive(signal)) return false;
  if (context.state === 'suspended') await context.resume().catch(() => {});
  return alive(signal);
}

/**
 * Loads an entry and hands it to the player, unless the session was cancelled while it loaded (then nothing is played).
 * @returns {Promise<boolean>} true when the entry was handed to the player
 */
export async function playLoaded({ load, play, signal }) {
  const entry = await load(signal);
  if (!entry || !alive(signal)) return false;
  await play(entry, signal);
  return true;
}
