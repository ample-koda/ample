// Live Preview: Stop must reach every point where a late task could still act (the load of the sound, the resume of the audio context, the wait for a normal
// build). Every await here is a promise the test holds, so the order of events is fixed, not timed.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createListenSession } from '../listen-session.mjs';
import { createListenBuilder, playLoaded, resumeUnlessCancelled, waitForFreeStore } from '../listen-cancel.mjs';

const deferred = () => { let resolve, reject; const promise = new Promise((a, b) => { resolve = a; reject = b; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 5; i++) await new Promise((resolve) => setImmediate(resolve)); };

/** A session wired like listen.js: a load that the test holds, then a player whose context resume the test holds. */
function rig() {
  const loads = [], resumes = [], plays = [], builds = [];
  const context = { state: 'suspended', resume: () => { const d = deferred(); resumes.push(d); return d.promise; } };
  const player = async (entry, signal) => {
    if (!(await resumeUnlessCancelled(context, signal))) return false;
    plays.push(entry);
    return true;
  };
  const session = createListenSession({
    build: async (job) => { builds.push(job); return { take: `take-${job.name}`, job }; },
    cancelBuild() {},
    play: (result, number, signal) => playLoaded({
      signal,
      load: () => { const d = deferred(); loads.push({ d, result, number }); return d.promise; },
      play: (entry, s) => player(entry, s),
    }),
    stopPlayback() {},
    setTimer: (fn) => fn,
    clearTimer() {},
  });
  return { session, loads, resumes, plays, builds, context };
}

test('Stop while the sound loads: when the load finishes nothing plays and the session stays stopped', async () => {
  const { session, loads, resumes, plays } = rig();
  const started = session.start({ name: 'a' });
  await flush();
  assert.equal(loads.length, 1, 'the build is done and the take is loading');
  session.stop();
  loads[0].d.resolve({ buffer: 'A' });
  await started;
  await flush();
  assert.deepEqual(plays, [], 'no audio was started');
  assert.equal(resumes.length, 0, 'the context was not even resumed for a stopped session');
  const state = session.state();
  assert.equal(state.status, 'stopped');
  assert.equal(state.active, false);
  assert.equal(state.playing, null);
});

test('Stop while the audio context resumes: when it resumes the entry is dropped, not queued or played', async () => {
  const { session, loads, resumes, plays } = rig();
  const started = session.start({ name: 'a' });
  await flush();
  loads[0].d.resolve({ buffer: 'A' });
  await flush();
  assert.equal(resumes.length, 1, 'the player waits for the context to resume');
  session.stop();
  resumes[0].resolve();
  await started;
  await flush();
  assert.deepEqual(plays, []);
  assert.equal(session.state().status, 'stopped');
  assert.equal(session.state().playing, null);
});

test('Stop, then Listen again: the old task finishing later starts no audio and changes nothing of the new session', async () => {
  const { session, loads, resumes, plays } = rig();
  const first = session.start({ name: 'old' });
  await flush();
  session.stop();
  const second = session.start({ name: 'new' });
  await flush();
  assert.equal(loads.length, 2, 'the old take is still loading, the new one loads too');
  loads[1].d.resolve({ buffer: 'NEW' });
  await flush();
  resumes[0].resolve();
  await second;
  assert.deepEqual(plays, [{ buffer: 'NEW' }]);
  const heard = session.state();
  assert.equal(heard.status, 'playing');
  assert.equal(heard.playing, 2, 'the number of the new build');
  // the old load finishes last
  loads[0].d.resolve({ buffer: 'OLD' });
  await first;
  await flush();
  assert.deepEqual(plays, [{ buffer: 'NEW' }], 'the old entry was never handed to the player');
  assert.equal(resumes.length, 1, 'and it did not touch the audio context');
  assert.deepEqual({ status: session.state().status, playing: session.state().playing, active: session.state().active }, { status: 'playing', playing: 2, active: true });
});

test('Stop, then Listen again: an old context resume that finishes late does not queue its entry in the new session', async () => {
  const { session, loads, resumes, plays } = rig();
  const first = session.start({ name: 'old' });
  await flush();
  loads[0].d.resolve({ buffer: 'OLD' });
  await flush();
  assert.equal(resumes.length, 1, 'the old task waits for the resume');
  session.stop();
  const second = session.start({ name: 'new' });
  await flush();
  loads[1].d.resolve({ buffer: 'NEW' });
  await flush();
  resumes[1].resolve();
  await second;
  assert.deepEqual(plays, [{ buffer: 'NEW' }]);
  resumes[0].resolve();   // the old resume finishes after the new session already plays
  await first;
  await flush();
  assert.deepEqual(plays, [{ buffer: 'NEW' }]);
  assert.equal(session.state().playing, 2);
});

test('a load that fails in an old session is not an error of the new session, a load that fails in the current one is', async () => {
  const { session, loads } = rig();
  const first = session.start({ name: 'old' });
  await flush();
  session.stop();
  const second = session.start({ name: 'new' });
  await flush();
  loads[0].d.reject(new Error('the sound could not be read'));
  await first;   // does not reject: nobody listens to the old session any more
  await flush();
  assert.equal(session.state().error, null);
  assert.notEqual(session.state().status, 'error');
  loads[1].d.reject(new Error('the sound of the new take could not be read'));
  await second;
  assert.equal(session.state().status, 'error', 'a failure of the current session is reported as its status');
  assert.match(session.state().error, /new take/);
});

test('every task gets the token of its session; Stop and a new Listen cancel the old one', async () => {
  const seen = [];
  const session = createListenSession({
    build: async (job, signal) => { seen.push(signal); return { take: job.name }; },
    cancelBuild() {},
    play: async () => {},
    stopPlayback() {},
  });
  await session.start({ name: 'a' });
  session.stop();
  await session.start({ name: 'b' });
  await session.start({ name: 'c' });   // a new Listen without Stop ends the session of b too
  assert.equal(seen.length, 3);
  assert.deepEqual(seen.map((s) => s.cancelled), [true, true, false]);
});

// ---- the wait for a build that is not ours ----
function waitRig() {
  const store = { busy: true };
  const sleeps = [], built = [], stopped = [];
  const builder = createListenBuilder({
    isBusy: () => store.busy,
    buildNow: async (job) => { built.push(job); return { take: job.name }; },
    stopStore: () => stopped.push('stopBuild'),
    sleep: () => { const d = deferred(); sleeps.push(d); return d.promise; },
  });
  const session = createListenSession({
    build: (job, signal) => builder.build(job, signal),
    cancelBuild: () => builder.cancel(),
    play: async () => {},
    stopPlayback() {},
  });
  return { store, sleeps, built, stopped, session };
}

test('Stop while waiting for a normal build: the waiting task is withdrawn, the normal build is left running, and no listening build starts afterwards', async () => {
  const { store, sleeps, built, stopped, session } = waitRig();
  const started = session.start({ name: 'a' });
  await flush();
  assert.equal(sleeps.length, 1, 'the task waits for the normal build');
  session.stop();
  assert.deepEqual(stopped, [], 'Stop did not cancel the normal build');
  store.busy = false;   // the normal build ends
  sleeps[0].resolve();
  await started;
  await flush();
  assert.deepEqual(built, [], 'the withdrawn task never started a listening build');
  assert.deepEqual(stopped, []);
  assert.equal(session.state().status, 'stopped');
});

test('Stop while a build of our own runs still cancels that build', async () => {
  const stopped = [], gate = deferred();
  const builder = createListenBuilder({ isBusy: () => false, buildNow: () => gate.promise, stopStore: () => stopped.push('stopBuild') });
  const session = createListenSession({ build: (job, signal) => builder.build(job, signal), cancelBuild: () => builder.cancel(), play: async () => {}, stopPlayback() {} });
  const started = session.start({ name: 'a' });
  await flush();
  session.stop();
  assert.deepEqual(stopped, ['stopBuild']);
  gate.resolve({ take: 'late' });
  await started;
  assert.equal(session.state().status, 'stopped');
});

test('Stop, then Listen while waiting for a normal build: only the new task builds, once, when the store is free', async () => {
  const { store, sleeps, built, stopped, session } = waitRig();
  const first = session.start({ name: 'old' });
  await flush();
  session.stop();
  const second = session.start({ name: 'new' });
  await flush();
  assert.equal(sleeps.length, 2, 'the old and the new task both wait');
  store.busy = false;
  sleeps[0].resolve();
  await first;
  assert.deepEqual(built, [], 'the old task does not build');
  sleeps[1].resolve();
  await second;
  assert.deepEqual(built, [{ name: 'new' }]);
  assert.deepEqual(stopped, []);
  assert.equal(session.state().status, 'playing');
});

test('waitForFreeStore: true when the store is free, false when the task was cancelled while it waited', async () => {
  const sleeps = [], store = { busy: true }, signal = { cancelled: false };
  const sleep = () => { const d = deferred(); sleeps.push(d); return d.promise; };
  const waiting = waitForFreeStore(() => store.busy, signal, { sleep });
  await flush();
  assert.equal(sleeps.length, 1);
  signal.cancelled = true;
  sleeps[0].resolve();
  assert.equal(await waiting, false);
  assert.equal(sleeps.length, 1, 'it did not wait again');
  assert.equal(await waitForFreeStore(() => false, { cancelled: false }, { sleep }), true);
  assert.equal(await waitForFreeStore(() => false, { cancelled: true }, { sleep }), false, 'a cancelled task does not go on even when the store is free');
});

test('a build that was made before plays at once, without a new build', async () => {
  let builds = 0;
  const played = [];
  const session = createListenSession({
    build: async () => { builds++; return { take: 'new' }; }, cancelBuild() {}, stopPlayback() {},
    play: async (result) => { played.push(result.take); },
  });
  await session.start({ key: 'k' }, { take: 'prepared' });
  assert.equal(builds, 0);
  assert.deepEqual(played, ['prepared']);
  assert.equal(session.state().status, 'playing');
  session.stop();
  await session.start({ key: 'k' });
  assert.equal(builds, 1, 'without a prepared build the session builds');
  assert.deepEqual(played, ['prepared', 'new']);
});
