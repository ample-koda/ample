import test from 'node:test';
import assert from 'node:assert/strict';
import { createPerformance, nextBoundary, performanceEnabled } from '../../app/performance.js';

function rig() {
  let t = 100; const log = [];
  const play = (clip, when, lane) => {
    const h = { clip: clip.id, lane, when, stopped: null, cancelled: false,
      stop(w) { this.stopped = w ?? t; }, cancel() { this.cancelled = true; } };
    log.push(h); return h;
  };
  const p = createPerformance({ now: () => t, play, bpm: 120, beatsPerBar: 4 });
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
