import test from 'node:test';
import assert from 'node:assert/strict';
import { createRecorder, capturedParts, capturedOps, recordEngine } from '../../app/performance-capture.js';

const runs = (c) => c.runs.map((r) => `${r.clip}:${r.from}-${r.to}`);

test('start and stop are recorded at the confirmed beats, relative to Record start', () => {
  const r = createRecorder(); r.start(10);
  assert.equal(r.confirm('start', 'A', 12), true); assert.equal(r.confirm('stop', 'A', 18), true);
  assert.deepEqual(runs(r.finish(30)), ['A:2-8']);
});

test('a replaced clip ends at the replacement beat (same clip, and a different clip in the same lane)', () => {
  const r = createRecorder(); r.start(0);
  r.confirm('start', 'A', 0); r.confirm('start', 'A2', 0, 'bass'); r.confirm('start', 'A', 4);   // same clip launched again: restart
  r.confirm('start', 'B2', 8, 'bass'); // lane replacement: B2 ends A2 although A2 was never stopped
  assert.deepEqual(runs(r.finish(12)), ['A:0-4', 'A2:0-8', 'A:4-12', 'B2:8-12']);
});

test('same clip id in two lanes does not collide', () => {
  const r = createRecorder(); r.start(0);
  r.confirm('start', 'A', 0, 'drums'); r.confirm('start', 'A', 4, 'bass'); r.confirm('stop', 'A', 8, 'drums');
  assert.deepEqual(runs(r.finish(16)), ['A:0-8', 'A:4-16']);
});

test('a stop for a clip that is not playing, or that was replaced, records nothing', () => {
  const r = createRecorder(); r.start(0);
  assert.equal(r.confirm('stop', 'A', 2), false);
  r.confirm('start', 'B', 0, 'l'); r.confirm('start', 'C', 4, 'l');
  assert.equal(r.confirm('stop', 'B', 4, 'l'), false);                    // stale stop of the replaced clip
  assert.deepEqual(runs(r.finish(8)), ['B:0-4', 'C:4-8']);
});

test('nothing is stored before start, for bad input, or for unknown kinds', () => {
  const r = createRecorder();
  assert.equal(r.confirm('start', 'A', 1), false); assert.equal(r.recording, false);
  r.start(0);
  assert.equal(r.confirm('start', 'A', NaN), false); assert.equal(r.confirm('jump', 'A', 1), false);
  assert.deepEqual(r.finish(4).runs, []);
});

test('a cancelled queued launch leaves no ghost clip (nothing confirmed, nothing stored)', () => {
  const r = createRecorder(); r.start(0);
  r.confirm('start', 'bed', 0); r.cancel('B');
  assert.deepEqual(runs(r.finish(8)), ['bed:0-8']);
});

test('clips still playing at stop are closed at the stop beat; a zero-length run is dropped', () => {
  const r = createRecorder(); r.start(0);
  r.confirm('start', 'A', 0); r.confirm('start', 'B', 8); r.confirm('start', 'C', 8); r.confirm('start', 'C2', 8, 'x');
  assert.deepEqual(runs(r.finish(8)), ['A:0-8']);                         // B, C, C2 would start exactly at the stop beat
});

test('a bad or early stop beat still closes open clips and never goes back in time', () => {
  let r = createRecorder(); r.start(0); r.confirm('start', 'A', 4); r.confirm('start', 'B', 12);
  assert.deepEqual(runs(r.finish(NaN)), ['A:4-12']);                      // closed at the last known beat, not lost
  r = createRecorder(); r.start(0); r.confirm('start', 'A', 8); r.confirm('stop', 'A', 6);
  assert.deepEqual(runs(r.finish(20)), []);                               // out-of-order stop is treated as 8: zero long
  r = createRecorder(); r.start(5); r.confirm('start', 'A', 2);           // confirmed before Record started: clamps to the take start
  assert.deepEqual(runs(r.finish(9)), ['A:0-4']);
});

test('clips already playing when Record is pressed are captured from the start of the take', () => {
  const r = createRecorder(); r.start(16, [{ clip: 'bed', lane: 'drums' }, { clip: 'pad' }]);
  r.confirm('start', 'bed2', 20, 'drums');
  assert.deepEqual(runs(r.finish(24)), ['bed:0-4', 'pad:0-8', 'bed2:4-8']);
});

test('stopAll closes everything at that beat and recording continues', () => {
  const r = createRecorder(); r.start(0);
  r.confirm('start', 'A', 0); r.confirm('start', 'B', 2, 'b');
  assert.equal(r.stopAll(6), true); assert.equal(r.recording, true);
  r.confirm('start', 'A', 8);
  assert.deepEqual(runs(r.finish(12)), ['A:0-6', 'B:2-6', 'A:8-12']);
});

test('the recorder can be reused: a second take starts clean, finish twice is empty', () => {
  const r = createRecorder(); r.start(0); r.confirm('start', 'A', 0); r.finish(4);
  assert.deepEqual(r.finish(8).runs, []);
  r.start(100); r.confirm('start', 'B', 101);
  assert.deepEqual(runs(r.finish(105)), ['B:1-5']);
  r.start(0); r.confirm('start', 'A', 0); r.start(0);                     // start again while recording discards the take
  assert.deepEqual(r.finish(4).runs, []);
});

test('capturedParts snaps to the nearest bar, one bar at least, 1-based', () => {
  const p = capturedParts({ beatsPerBar: 4, runs: [
    { clip: 'A', from: 0, to: 8 },
    { clip: 'B', from: 3.9, to: 8 },           // starts almost on bar 2: bar 2, not bar 1
    { clip: 'C', from: 3.9999999999, to: 12 }, // float noise on a bar line
    { clip: 'D', from: 8, to: 8 },             // zero length: one bar
    { clip: 'E', from: 8, to: 9.5 },           // shorter than a bar: one bar
    { clip: 'F', from: 0, to: 1 },
  ] });
  assert.deepEqual(p.A, [{ at: 1, length: 2, name: '' }]);
  assert.deepEqual(p.B, [{ at: 2, length: 1, name: '' }]);
  assert.deepEqual(p.C, [{ at: 2, length: 2, name: '' }]);
  assert.deepEqual(p.D, [{ at: 3, length: 1, name: '' }]);
  assert.deepEqual(p.E, [{ at: 3, length: 1, name: '' }]);
  assert.deepEqual(p.F, [{ at: 1, length: 1, name: '' }]);
});

test('capturedParts never overlaps parts of one block; other beats-per-bar and junk are handled', () => {
  const p = capturedParts({ beatsPerBar: 3, runs: [
    { clip: 'A', from: 0, to: 0.5 },   // snaps to bar 1 (min one bar)
    { clip: 'A', from: 0.5, to: 7 },   // starts in bar 1 as well: merged into one part, bars 1-2
    { clip: 'A', from: 12, to: 15 },
    { clip: 'A', from: NaN, to: 3 },
  ] });
  assert.deepEqual(p.A, [{ at: 1, length: 2, name: '' }, { at: 5, length: 1, name: '' }]);
  for (const parts of Object.values(p)) {
    for (let i = 1; i < parts.length; i++) assert.ok(parts[i].at >= parts[i - 1].at + parts[i - 1].length);
  }
  assert.deepEqual(capturedParts(undefined), {});
  assert.deepEqual(capturedParts({ runs: [{ clip: 'A', from: 0, to: 4 }] }).A, [{ at: 1, length: 1, name: '' }]);
});

test('capturedOps is one list, one setParts per block (one editAll = one undo step)', () => {
  const ops = capturedOps({
    beatsPerBar: 4,
    runs: [{ clip: 'A', from: 0, to: 4 }, { clip: 'B', from: 4, to: 8 }, { clip: 'A', from: 8, to: 12 }],
  });
  assert.deepEqual(ops, [
    { type: 'setParts', block: 'A', parts: [{ at: 1, length: 1, name: '' }, { at: 3, length: 1, name: '' }] },
    { type: 'setParts', block: 'B', parts: [{ at: 2, length: 1, name: '' }] },
  ]);
  assert.deepEqual(capturedOps({ runs: [] }), []);
});

// A stand-in with the interface and behaviour of createPerformance in web/app/performance.js: launch returns the
// boundary (null when the clip plays and a queued
// replacement is taken back), launchBatch(at, batch), cancelQueued, stopAll, state() moves due launches on. Grid: 2 s = 4 beats at 120 bpm.
function fakeEngine(clock) {
  const lanes = new Map();
  const lane = (n) => lanes.get(n) || lanes.set(n, { clips: new Set(), playing: null, queued: null, stopAt: null }).get(n);
  const tick = () => {
    for (const l of lanes.values()) {
      if (l.stopAt !== null && l.stopAt <= clock.t) { l.playing = null; l.stopAt = null; }
      if (l.queued && l.queued.when <= clock.t) { l.playing = l.queued.clip; l.queued = null; l.stopAt = null; }
    }
  };
  return {
    bpm: 120,
    launch(n, clip) {
      tick();
      const l = lane(n);
      l.clips.add(clip);
      if (l.playing === clip) { l.queued = null; return null; }
      const when = Math.ceil(clock.t / 2) * 2;
      l.queued = { clip, when };
      return when;
    },
    launchBatch(at, batch) {
      tick();
      for (const b of batch) {
        const l = lane(b.lane);
        if (b.action === 'start') {
          l.clips.add(b.clip);
          if (l.playing !== b.clip) l.queued = { clip: b.clip, when: at };
        } else if (b.action === 'stop' && l.playing) {
          l.queued = null;
          l.stopAt = at;
        }
      }
      return at;
    },
    cancelQueued(n) { tick(); const l = lanes.get(n); if (!l?.queued) return false; l.queued = null; return true; },
    stopAll() { for (const l of lanes.values()) { l.queued = null; l.playing = null; l.stopAt = null; } },
    setTempo() {},
    state() {
      tick();
      const o = {};
      for (const [n, l] of lanes) {
        o[n] = [...l.clips].map((id) => ({ id, status: l.playing === id ? 'playing' : l.queued?.clip === id ? 'queued' : 'ready' }));
      }
      return o;
    },
  };
}

test('recordEngine: confirmed at the boundary in beats, cancelled launches and replaced queued launches leave nothing', () => {
  const clock = { t: 100 }; const eng = fakeEngine(clock); const rec = createRecorder();
  const rr = recordEngine(eng, rec, { now: () => clock.t });
  rr.start();
  clock.t = 100.5; assert.equal(rr.launch('drums', 'A'), 102);           // queued: not recorded yet
  clock.t = 101; rr.launch('bass', 'X'); assert.equal(rr.cancelQueued('bass'), true);   // cancelled: ghost-free
  rr.launch('drums', 'B');                                               // replaces the queued A in the lane
  clock.t = 102; rr.sync();
  clock.t = 103; assert.equal(rr.cancelQueued('drums'), false);          // already audible: cannot be cancelled
  clock.t = 104.5; rr.launch('drums', 'C'); clock.t = 105; assert.equal(rr.cancelQueued('drums'), true);
  clock.t = 106;
  assert.deepEqual(runs(rr.finish()), ['B:4-12']);                       // B from beat 4 (102 s) to the stop at beat 12 (106 s)
});

test('recordEngine: launching what plays takes back a queued replacement; nothing is recorded for it', () => {
  const clock = { t: 100 }; const eng = fakeEngine(clock); const rec = createRecorder();
  const rr = recordEngine(eng, rec, { now: () => clock.t });
  rr.start(); rr.launch('drums', 'A');                                   // 100 is a boundary: audible now
  clock.t = 101; rr.launch('drums', 'B'); assert.equal(rr.launch('drums', 'A'), null);
  clock.t = 103;
  assert.deepEqual(runs(rr.finish()), ['A:0-6']);
});

test('recordEngine: stopAll closes clips at the stop beat; a clip still queued is not recorded', () => {
  const clock = { t: 100 }; const eng = fakeEngine(clock); const rec = createRecorder();
  const rr = recordEngine(eng, rec, { now: () => clock.t });
  rr.start(); rr.launch('drums', 'A');
  clock.t = 103; rr.stopAll();
  clock.t = 104.5; rr.launch('drums', 'B'); clock.t = 105; rr.stopAll();
  assert.deepEqual(runs(rr.finish()), ['A:0-6']);
});

test('recordEngine: a scene batch starts and stops lanes together, in time order', () => {
  const clock = { t: 100 }; const eng = fakeEngine(clock); const rec = createRecorder();
  const rr = recordEngine(eng, rec, { now: () => clock.t });
  rr.start();
  clock.t = 100.5; rr.launchBatch(102, [{ lane: 'drums', action: 'start', clip: 'A' }, { lane: 'bass', action: 'start', clip: 'X' }]);
  clock.t = 103; rr.launchBatch(104, [{ lane: 'drums', action: 'start', clip: 'B' }, { lane: 'bass', action: 'stop' }]);
  clock.t = 104.5; rr.sync();
  clock.t = 105; rr.launchBatch(104.9, [{ lane: 'drums', action: 'start', clip: 'B' }]);   // B plays already: nothing new
  clock.t = 106;
  assert.deepEqual(runs(rr.finish()), ['A:4-8', 'X:4-8', 'B:8-12']);
});

test('recordEngine: a batch time that has just passed (late tick) is recorded at that time', () => {
  const clock = { t: 100 }; const eng = fakeEngine(clock); const rec = createRecorder();
  const rr = recordEngine(eng, rec, { now: () => clock.t });
  rr.start(); clock.t = 101; rr.launchBatch(100.5, [{ lane: 'drums', action: 'start', clip: 'A' }]);
  clock.t = 103;
  assert.deepEqual(runs(rr.finish()), ['A:1-6']);
});
