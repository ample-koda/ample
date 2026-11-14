import test from 'node:test';
import assert from 'node:assert/strict';
import { createLiveSwap } from '../live-swap.mjs';

function rig(over = {}) {
  const log = [];
  const Q = { live: true, liveBusy: false, liveAgain: false, error: '', wroteLive: false, builtTake: null, stale: true };
  let playing = true;
  let take = { id: 't1' };
  const deps = {
    state: Q,
    isPlaying: () => playing,
    write: async () => { log.push('write'); },
    build: async () => { log.push('build'); },
    takeOf: () => take,
    swap: async (t) => { log.push('swap ' + t.id); },
    setStatus: (s) => log.push('status:' + s),
    ...over,
  };
  return { Q, log, run: createLiveSwap(deps), stop: () => { playing = false; }, setTake: (t) => { take = t; } };
}

test('Live on and playing: write, build and swap the take', async () => {
  const r = rig();
  await r.run();
  assert.deepEqual(r.log, ['status:building the change...', 'write', 'build', 'swap t1', 'status:the change plays when the loop comes round']);
  assert.equal(r.Q.liveBusy, false);
});

test('Live off before the swap starts: nothing is written, built or swapped', async () => {
  const r = rig();
  r.Q.live = false;
  await r.run();
  assert.deepEqual(r.log, []);
});

test('Live switched off while the build runs: the take is not swapped and not remembered as built', async () => {
  let r;
  r = rig({ build: async () => { r.log.push('build'); r.Q.live = false; } });
  await r.run();
  assert.deepEqual(r.log, ['status:building the change...', 'write', 'build', 'status:']);
  assert.equal(r.Q.builtTake, null);
  assert.equal(r.Q.stale, true, 'the next Play still rebuilds');
  assert.equal(r.Q.liveBusy, false);
});

test('Live switched off after a second change queued up: the queued rebuild never runs and nothing is swapped', async () => {
  let r;
  let builds = 0;
  r = rig({ build: async () => { builds++; r.log.push('build'); r.Q.liveAgain = true; r.Q.live = false; } });
  await r.run();
  assert.equal(builds, 1);
  assert.ok(!r.log.some((l) => l.startsWith('swap')));
});

test('Live switched off at the last moment, between the check and the swap, still stops the swap (checked twice after the build)', async () => {
  let r;
  r = rig({ takeOf: () => { r.Q.live = false; return { id: 't9' }; } });
  await r.run();
  assert.ok(!r.log.some((l) => l.startsWith('swap')));
  assert.equal(r.log.at(-1), 'status:');
});

test('a change that arrives during a build is built again and only the last take is swapped', async () => {
  let r;
  let n = 0;
  r = rig({ build: async () => { n++; r.log.push('build'); if (n === 1) { await r.run(); } } });
  await r.run();
  assert.equal(r.log.filter((l) => l === 'build').length, 2);
  assert.deepEqual(r.log.filter((l) => l.startsWith('swap')), ['swap t1']);
});

test('playback stopped meanwhile: nothing starts', async () => {
  const r = rig();
  r.stop();
  await r.run();
  assert.deepEqual(r.log, []);
});
