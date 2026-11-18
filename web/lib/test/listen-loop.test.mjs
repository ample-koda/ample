// The loop engine shared by Studio and Remix, with a fake audio context whose clock the test moves and whose timer it calls by hand.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createLoopPlayer } from '../listen-loop.mjs';

function rig({ state = 'running' } = {}) {
  const started = [], stopped = [], before = [], timers = [];
  const context = {
    currentTime: 0, state,
    resume: async () => { context.state = 'running'; },
    createGain: () => ({ gain: { setValueAtTime() {}, linearRampToValueAtTime() {} }, connect: (to) => to }),
    createBufferSource: () => {
      const source = { buffer: null, connect: (to) => to, start: (at, from, length) => started.push({ at, from, length, buffer: source.buffer }), stop: () => stopped.push(source) };
      return source;
    },
  };
  const player = createLoopPlayer({
    context: () => context, output: () => ({}), beforeStart: () => before.push(context.currentTime),
    setTimer: (fn) => { timers.push(fn); return timers.length; }, clearTimer() {},
  });
  const tick = () => timers[timers.length - 1]?.();
  return { context, player, started, stopped, before, tick };
}
const entry = (name, ...lengths) => {
  let at = 0;
  return { take: name, buffer: name, segments: lengths.map((l) => { const s = { from: at, to: at + l }; at += l; return s; }) };
};

test('an entry starts from silence 30 ms ahead, plays its segments one after the other and tells the player to take over', async () => {
  const { player, started, before } = rig();
  await player.playEntry(entry('A', 1, 2));
  assert.deepEqual(started.map((s) => [s.at, s.from, s.length, s.buffer]), [[0.03, 0, 1, 'A'], [1.03, 1, 2, 'A']]);
  assert.equal(before.length, 1);
  assert.equal(player.state().playing, true);
});

test('the next lap is scheduled before the loop ends, without a gap', async () => {
  const { context, player, started, tick } = rig();
  await player.playEntry(entry('A', 2));
  context.currentTime = 1.0; tick();
  assert.equal(started.length, 1, 'too early: the lap is not near its end');
  context.currentTime = 1.9; tick();
  assert.equal(started.length, 2);
  assert.ok(Math.abs(started[1].at - 2.03) < 1e-9, 'the second lap starts where the first ends');
});

test('a new entry waits for the loop boundary and comes in there', async () => {
  const { context, player, started, before, tick } = rig();
  await player.playEntry(entry('A', 2));
  context.currentTime = 0.5;
  await player.playEntry(entry('B', 3));
  assert.equal(player.state().pending, true, 'queued, A still plays');
  assert.equal(started.length, 1);
  assert.equal(before.length, 1, 'only the start from silence calls beforeStart');
  context.currentTime = 1.9; tick();
  assert.equal(started[1].buffer, 'B', 'B is scheduled in the look-ahead');
  assert.ok(Math.abs(started[1].at - 2.03) < 1e-9);
  assert.equal(player.state().pending, true, 'but A is still what is heard until the boundary');
  context.currentTime = 2.1; tick();
  assert.equal(player.state().pending, false);
});

test('timing now swaps at once at the same place in the loop', async () => {
  const { context, player, started, stopped } = rig();
  await player.playEntry(entry('A', 4));
  context.currentTime = 1.03;   // one second into the loop
  await player.playEntry(entry('B', 4), { timing: 'now' });
  assert.equal(stopped.length, 1, 'the old sound is faded out');
  const swapped = started[started.length - 1];
  assert.equal(swapped.buffer, 'B');
  assert.ok(Math.abs(swapped.from - 1) < 1e-9, 'B starts one second in, where A was');
});

test('Apply now brings the waiting entry in at once', async () => {
  const { context, player, started } = rig();
  await player.playEntry(entry('A', 4));
  context.currentTime = 2.03;
  await player.playEntry(entry('B', 4));
  assert.equal(player.state().pending, true);
  await player.applyPendingNow();
  assert.equal(player.state().pending, false);
  assert.equal(started[started.length - 1].buffer, 'B');
});

test('stop silences the sound, and tells the owner only when it was not the owner that asked', async () => {
  const { player, stopped } = rig();
  let told = 0;
  await player.playEntry(entry('A', 2), { onStop: () => told++ });
  player.stop({ quiet: true });
  assert.equal(told, 0);
  assert.equal(stopped.length, 1);
  assert.equal(player.state().playing, false);
  await player.playEntry(entry('A', 2), { onStop: () => told++ });
  player.stop();
  assert.equal(told, 1);
});

test('a session that was cancelled while the context resumes plays and queues nothing', async () => {
  const { context, player, started } = rig({ state: 'suspended' });
  const signal = { cancelled: false };
  let release;
  context.resume = () => new Promise((resolve) => { release = () => { context.state = 'running'; resolve(); }; });
  const playing = player.playEntry(entry('A', 2), { signal });
  signal.cancelled = true;
  release();
  assert.equal(await playing, false);
  assert.equal(started.length, 0);
  assert.equal(player.state().playing, false);
});

test('the state says where the playhead is: the take time follows the context clock and skips the silence between segments', async () => {
  const { context, player } = rig();
  const e = { take: 'A', buffer: 'A', segments: [{ from: 0, to: 1 }, { from: 3, to: 4 }] };   // 2 s heard, 2 s skipped
  await player.playEntry(e);
  context.currentTime = 1.53;   // 1.5 s into the heard loop: half a second into the second segment
  const s = player.state();
  assert.ok(Math.abs(s.takeTime - 3.5) < 1e-9);
  assert.ok(Math.abs(s.previewTime - 1.5) < 1e-9);
});

test('the audible lap changes at the boundary, not when the next lap is scheduled: state before, at and after the boundary', async () => {
  const { context, player, tick } = rig();
  const A = entry('A', 2), B = entry('B', 3);
  await player.playEntry(A);                                   // A is heard from 0.03 to 2.03
  context.currentTime = 0.5;
  await player.playEntry(B);
  context.currentTime = 1.9; tick();                           // B is scheduled in the look-ahead window
  let s = player.state();
  assert.equal(s.entry, A, 'before the boundary: A is the entry');
  assert.equal(s.pending, true);
  assert.ok(Math.abs(s.remaining - 0.13) < 1e-9, 'the time left is that of the lap that is heard');
  assert.ok(Math.abs(s.previewTime - 1.87) < 1e-9);
  assert.equal(s.takeTime, 1.87, 'the take time is that of A');
  context.currentTime = 2.0299;
  assert.equal(player.state().entry, A, 'just before the boundary');
  context.currentTime = 2.03;
  s = player.state();
  assert.equal(s.entry, B, 'at the boundary B is the entry');
  assert.equal(s.pending, false);
  assert.ok(Math.abs(s.previewTime) < 1e-9 && Math.abs(s.remaining - 3) < 1e-9, 'a new lap: the time starts again');
  context.currentTime = 2.5; tick();
  assert.equal(player.state().entry, B);
});

test('Apply now inside the look-ahead window brings the waiting take in at the same place of the loop (the scheduled lap is replaced)', async () => {
  const { context, player, started, stopped, tick } = rig();
  await player.playEntry(entry('A', 2));
  context.currentTime = 0.5;
  await player.playEntry(entry('B', 4));
  context.currentTime = 1.9; tick();                           // B is scheduled for 2.03, not audible yet
  assert.equal(player.state().pending, true);
  const before = started.length;
  await player.applyPendingNow();
  assert.ok(stopped.length >= 2, 'the sources of A and of the scheduled lap are faded out');
  assert.equal(started[before].buffer, 'B');
  assert.ok(Math.abs(started[before].from - 1.87) < 1e-9, 'B comes in at the place of the loop that A had reached');
  assert.equal(player.state().entry.take, 'B');
  assert.equal(player.state().pending, false);
});

test('a take that arrives after the next lap was scheduled waits for the lap after it, and the heard take does not change early', async () => {
  const { context, player, started, tick } = rig();
  await player.playEntry(entry('A', 2));
  context.currentTime = 1.9; tick();                           // A is scheduled again for 2.03
  await player.playEntry(entry('B', 2));                       // too late for this boundary
  assert.equal(player.state().entry.take, 'A');
  assert.equal(player.state().pending, true);
  context.currentTime = 2.1; tick();
  assert.equal(player.state().entry.take, 'A', 'the lap that was scheduled is A again');
  context.currentTime = 3.95; tick();
  assert.equal(started.at(-1).buffer, 'B', 'B is scheduled for the next boundary');
  context.currentTime = 4.1; tick();
  assert.equal(player.state().entry.take, 'B');
});
