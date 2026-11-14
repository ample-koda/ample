import test from 'node:test';
import assert from 'node:assert/strict';
import { createPerformance, nextBoundary, performanceEnabled, setPerformanceEnabled } from '../../app/performance.js';

function rig(extra = {}) {
  let t = 100; const log = [];
  const play = (clip, when, lane) => {
    const h = { clip: clip.id, lane, when, stopped: null, cancelled: false,
      stopCalls: [], stopCancelled: false,
      stop(w) { this.stopped = w ?? t; this.stopCalls.push(w); },
      cancel() { this.cancelled = true; },
      cancelStop() { this.stopped = null; this.stopCancelled = true; },
    };
    log.push(h); return h;
  };
  const p = createPerformance({ now: () => t, play, bpm: 120, beatsPerBar: 4, startLead: 0, ...extra });
  for (const [l, id] of [['drums', 'A'], ['drums', 'B'], ['bass', 'A']]) p.addClip(l, { id });
  return { p, log, at: (v) => { t = v; } };
}

test('off by default, on only with the stored flag', () => {
  assert.equal(performanceEnabled({ getItem: () => null }), false);
  assert.equal(performanceEnabled({ getItem: () => '1' }), true);
});
test('boundaries snap to the next bar', () => {
  assert.equal(nextBoundary(101, 100, 0.5, 4, 'bar'), 102);
  assert.equal(nextBoundary(102, 100, 0.5, 4, 'bar'), 102);
  assert.equal(nextBoundary(100.6, 100, 0.5, 4, 'beat'), 101);
});
test('queue drums B mid-bar: only drums change on the downbeat', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); p.launch('bass', 'A'); at(102);
  at(103); // halfway through bar 2 (bar = 2s from origin 100)
  const when = p.launch('drums', 'B');
  assert.equal(when, 104);
  assert.equal(log.find((h) => h.clip === 'A' && h.lane === 'drums').stopped, 104);
  assert.equal(log.find((h) => h.lane === 'bass').stopped, null);
  assert.equal(p.state().drums.find((c) => c.id === 'B').status, 'queued');
  at(104);
  assert.equal(p.state().drums.find((c) => c.id === 'B').status, 'playing');
});
test('cancel a queued launch, and stopAll clears queued and playing', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); at(102); p.launch('bass', 'A'); at(103);
  p.launch('drums', 'B');
  assert.equal(p.cancelQueued('drums'), true);
  assert.equal(log.at(-1).cancelled, true);
  p.launch('drums', 'B');
  p.stopAll();
  for (const l of Object.values(p.state())) for (const c of l) assert.equal(c.status, 'ready');
});

test('opt-in flag: default off, off for anything but 1, survives broken storage, writes through', () => {
  assert.equal(performanceEnabled(undefined), false);
  assert.equal(performanceEnabled({ getItem: () => 'true' }), false);
  assert.equal(performanceEnabled({ getItem: () => { throw new Error('blocked'); } }), false);
  const m = new Map(); const st = { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, v) };
  setPerformanceEnabled(st, true); assert.equal(performanceEnabled(st), true);
  setPerformanceEnabled(st, false); assert.equal(performanceEnabled(st), false);
  setPerformanceEnabled({ setItem() { throw new Error('blocked'); } }, true);
});
test('launch at the boundary starts now; just after it waits for the next one; before the origin gives the origin', () => {
  assert.equal(nextBoundary(104, 100, 0.5, 4, 'bar'), 104);
  assert.equal(nextBoundary(104.001, 100, 0.5, 4, 'bar'), 106);
  assert.equal(nextBoundary(90, 100, 0.5, 4, 'bar'), 100);
  assert.equal(nextBoundary(-3, -10, 0.5, 4, 'beat'), -3);
  assert.throws(() => nextBoundary(NaN, 100, 0.5, 4, 'bar'), RangeError);
  assert.throws(() => nextBoundary(1, 0, 0, 4, 'bar'), RangeError);
});
test('bad settings and a broken clock are refused', () => {
  const play = () => ({ stop() {}, cancelStop() {} });
  assert.throws(() => createPerformance({ now: () => 0, play, bpm: 0 }), RangeError);
  assert.throws(() => createPerformance({ now: () => 0, play, bpm: NaN }), RangeError);
  assert.throws(() => createPerformance({ now: () => 0, play, beatsPerBar: 0 }), RangeError);
  assert.throws(() => createPerformance({ now: () => 0, play, quantize: 'bogus' }), RangeError);
  const p = createPerformance({ now: () => NaN, play }); p.addClip('a', { id: 1 });
  assert.throws(() => p.launch('a', 1), RangeError);
});
test('Drums A and Bass A launched a few milliseconds apart start on the same downbeat', () => {
  let t = 50;
  const play = () => ({ stop() {}, cancel() {}, cancelStop() {} });
  const p = createPerformance({ now: () => t, play }); // default startLead
  p.addClip('drums', { id: 'A' }); p.addClip('bass', { id: 'A' });
  const a = p.launch('drums', 'A'); t = 50.012; const b = p.launch('bass', 'A');
  assert.equal(a, b); assert.ok(a > 50.012);
});
test('cancelling a queued replacement keeps the playing clip playing (its stop is taken back)', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); at(103);
  p.launch('drums', 'B');
  const a = log.find((h) => h.clip === 'A');
  assert.equal(a.stopped, 104);
  assert.equal(p.cancelQueued('drums'), true);
  assert.equal(a.stopCancelled, true); assert.equal(a.stopped, null);
  at(105);
  assert.equal(p.state().drums.find((c) => c.id === 'A').status, 'playing');
});
test('cancel at or after the start time is refused: the clip is already playing', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); at(103); p.launch('drums', 'B'); at(104);
  assert.equal(p.cancelQueued('drums'), false);
  assert.equal(log.find((h) => h.clip === 'B').cancelled, false);
  assert.equal(p.state().drums.find((c) => c.id === 'B').status, 'playing');
});
test('replacing stops the old clip and starts the new one at the same time', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); at(102.5);
  const when = p.launch('drums', 'B');
  assert.equal(log.find((h) => h.clip === 'B').when, when);
  assert.equal(log.find((h) => h.clip === 'A').stopped, when);
  p.launch('drums', 'A'); // change of mind before the boundary: A keeps playing, B is dropped
  assert.equal(log.find((h) => h.clip === 'B').cancelled, true);
  assert.equal(log.find((h) => h.clip === 'A').stopped, null);
  assert.equal(p.state().drums.find((c) => c.id === 'A').status, 'playing');
});
test('double launch of the same clip does nothing twice', () => {
  const { p, log, at } = rig();
  assert.equal(p.launch('drums', 'A'), 100); at(100);
  assert.equal(p.launch('drums', 'A'), null);
  at(101); const w = p.launch('drums', 'B');
  assert.equal(p.launch('drums', 'B'), w);
  assert.equal(log.filter((h) => h.clip === 'B').length, 1);
});
test('stopAll with a queued replacement cancels it once and stops the playing clip', () => {
  const { p, log, at } = rig();
  p.launch('drums', 'A'); at(101); p.launch('drums', 'B');
  p.stopAll();
  const b = log.find((h) => h.clip === 'B');
  assert.equal(b.cancelled, true); assert.deepEqual(b.stopCalls, []);
  assert.equal(log.find((h) => h.clip === 'A').stopped, 101);
  for (const l of Object.values(p.state())) for (const c of l) assert.equal(c.status, 'ready');
});
test('unknown lane or clip throws and does not create a lane', () => {
  const { p } = rig();
  assert.throws(() => p.launch('ghost', 'A'));
  assert.equal(p.state().ghost, undefined);
  assert.throws(() => p.launch('drums', 'Z'));
  assert.throws(() => p.addClip('drums', {}));
});
test('state() returns fresh objects: changing them changes nothing', () => {
  const { p, at } = rig();
  p.launch('drums', 'A'); at(100);
  const s = p.state(); s.drums[0].status = 'ready'; s.drums.length = 0; s.bass = null;
  assert.equal(p.state().drums.find((c) => c.id === 'A').status, 'playing');
  assert.ok(p.state().bass);
});
test('setTempo keeps the beat position and is refused while something is scheduled', () => {
  const { p, at } = rig();
  p.launch('drums', 'A'); at(101); // beat 2 of the grid at 120 bpm
  p.launch('drums', 'B');
  assert.throws(() => p.setTempo(60), /scheduled/);
  assert.throws(() => p.setTempo(-1), RangeError);
  p.cancelQueued('drums');
  p.setTempo(60); // beat 2 stays beat 2: one beat is now 1 s, so the grid starts at 99 and bars end at 103, 107
  assert.equal(p.nextBar(), 103);
});
test('launchBatch: one timestamp for start, stop and keep; a bad entry changes nothing', () => {
  const { p, log, at } = rig();
  p.addClip('fx', { id: 'F' });
  p.launch('drums', 'A'); p.launch('bass', 'A'); p.launch('fx', 'F'); at(101);
  p.launchBatch(102, [{ lane: 'drums', action: 'start', clip: 'B' }, { lane: 'bass', action: 'keep' }, { lane: 'fx', action: 'stop' }]);
  const h = (clip, lane) => log.find((x) => x.clip === clip && x.lane === lane);
  assert.equal(h('B', 'drums').when, 102);
  assert.equal(h('A', 'drums').stopped, 102);
  assert.equal(h('F', 'fx').stopped, 102);
  assert.equal(h('A', 'bass').stopped, null);
  const before = log.length;
  assert.throws(() => p.launchBatch(104, [{ lane: 'bass', action: 'stop' }, { lane: 'drums', action: 'start', clip: 'nope' }]));
  assert.equal(h('A', 'bass').stopped, null); assert.equal(log.length, before);
  assert.throws(() => p.launchBatch(NaN, []), RangeError);
  p.addClip('lead', { id: 'L' }); p.launchBatch(50, [{ lane: 'lead', action: 'start', clip: 'L' }]); // late: starts at once
  assert.equal(log.at(-1).when, 50);
});
