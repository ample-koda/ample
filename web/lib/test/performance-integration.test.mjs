// The three Performance modules as one design: the scene queue (issue 273) drives the launch engine (issue 266)
// and asks the readiness report (issue 272) for availability. The modules live on separate branches until they
// are merged, so this file skips itself while a sibling file is missing.
import test from 'node:test';
import assert from 'node:assert/strict';
import { createEngineSceneQueue } from '../../app/performance-scenes.js';

const load = (p) => import(p).catch(() => null);
const engineMod = await load('../../app/performance.js');
const setMod = await load('../performance-set.mjs');
const present = engineMod && setMod && engineMod.createPerformance && setMod.isAvailable;
const skip = present ? false : 'needs performance.js (266) and performance-set.mjs (272)';

const src = (take) => ({ kind: 'source', take });
const live = (t) => ({ ok: true, items: [src(t)] });
const conv = { ok: true, items: [src('x'), { kind: 'effect', tpl: 'convolution', id: 'c' }] };

function rig() {
  let t = 100; const log = [];
  const play = (clip, when, lane) => {
    const h = { clip: clip.id, lane, when, stopped: null, cancelled: false,
      stop(w) { this.stopped = w ?? t; }, cancel() { this.cancelled = true; }, cancelStop() { this.stopped = null; } };
    log.push(h); return h;
  };
  const engine = engineMod.createPerformance({ now: () => t, play, bpm: 120, beatsPerBar: 4, startLead: 0 });
  const clips = [
    { id: 'A', plan: live('a'), assets: new Set(['chain:a']) }, { id: 'B', plan: live('b'), assets: new Set(['chain:b']) },
    { id: 'C', plan: conv, assets: new Set(['chain:x']) }, // impulse missing, no prepared take: unavailable
  ];
  let report = setMod.prepareSet(clips, { sizes: new Map([['chain:a', 10], ['chain:b', 10]]), budget: 100 });
  for (const lane of ['drums', 'bass', 'vocal']) for (const c of clips) engine.addClip(lane, { id: c.id });
  const scenes = createEngineSceneQueue(engine, { available: (id) => setMod.isAvailable(report, id), leadSeconds: 0.1 });
  return { engine, scenes, log, at: (v) => { t = v; }, setReport: (r) => { report = r; }, get report() { return report; }, clips };
}
const h = (log, lane, clip) => log.find((x) => x.lane === lane && x.clip === clip);

test('scene: three lanes swap on one bar line, same timestamp, old clips stop at it', { skip }, () => {
  const r = rig();
  r.engine.launch('drums', 'A'); r.engine.launch('bass', 'A'); r.engine.launch('vocal', 'A'); r.at(100.5);
  const scene = {
    id: 'chorus',
    lanes: [{ lane: 'drums', intent: 'start', clip: 'B' }, { lane: 'bass', intent: 'start', clip: 'B' }, { lane: 'vocal', intent: 'keep' }],
  };
  const res = r.scenes.queue(scene);
  assert.equal(res.ok, true); assert.equal(res.at, 102);
  r.at(101.85); assert.equal(r.scenes.tick(), false);
  r.at(101.95); assert.equal(r.scenes.tick(), true);
  for (const lane of ['drums', 'bass']) { assert.equal(h(r.log, lane, 'B').when, 102); assert.equal(h(r.log, lane, 'A').stopped, 102); }
  assert.equal(h(r.log, 'vocal', 'A').stopped, null);
  assert.equal(r.log.filter((x) => x.clip === 'B').length, 2);
  r.at(102); r.scenes.settle();
  assert.equal(r.scenes.pending, null);
  assert.equal(r.engine.state().drums.find((c) => c.id === 'B').status, 'playing');
});
test('scene with an unavailable clip changes nothing; readiness change after queueing is caught at the next queue', { skip }, () => {
  const r = rig();
  r.engine.launch('drums', 'A'); r.at(100.5);
  const bad = r.scenes.queue({ lanes: [{ lane: 'drums', intent: 'start', clip: 'C' }] });
  assert.equal(bad.ok, false); assert.equal(bad.problems[0].clip, 'C');
  assert.equal(r.scenes.pending, null);
  r.setReport(setMod.invalidate(r.report, ['chain:b']));
  assert.equal(r.scenes.queue({ lanes: [{ lane: 'drums', intent: 'start', clip: 'B' }] }).ok, false);
  assert.equal(r.log.length, 1);
});
test('cancel before the deadline leaves the engine untouched; after it the engine holds the batch and Stop all clears it', { skip }, () => {
  const r = rig();
  r.engine.launch('drums', 'A'); r.at(100.5);
  const scene = { lanes: [{ lane: 'drums', intent: 'start', clip: 'B' }] };
  r.scenes.queue(scene); r.at(101.5); assert.equal(r.scenes.cancel(), true);
  r.at(101.95); r.scenes.tick(); assert.equal(r.log.filter((x) => x.clip === 'B').length, 0);
  r.scenes.queue(scene); r.scenes.tick(); // inside the deadline: handed to the engine
  assert.equal(r.scenes.cancel(), false);
  assert.equal(h(r.log, 'drums', 'B').when, 102);
  r.scenes.stopAll();
  assert.equal(h(r.log, 'drums', 'B').cancelled, true);
  assert.equal(r.scenes.pending, null);
  for (const l of Object.values(r.engine.state())) for (const c of l) assert.equal(c.status, 'ready');
});
test('Stop all while a scene is pending but not yet handed over: nothing launches afterwards', { skip }, () => {
  const r = rig();
  r.engine.launch('drums', 'A'); r.at(100.5);
  r.scenes.queue({ lanes: [{ lane: 'drums', intent: 'start', clip: 'B' }] });
  r.scenes.stopAll();
  r.at(101.95); assert.equal(r.scenes.tick(), false);
  assert.equal(r.log.filter((x) => x.clip === 'B').length, 0);
});
test('a scene stop action stops the lane at the bar line and a later scene can start it again', { skip }, () => {
  const r = rig();
  r.engine.launch('bass', 'A'); r.at(100.5);
  r.scenes.queue({ lanes: [{ lane: 'bass', intent: 'stop' }] }); r.at(101.95); r.scenes.tick();
  assert.equal(h(r.log, 'bass', 'A').stopped, 102);
  r.at(102.5); r.scenes.settle();
  assert.equal(r.engine.state().bass.find((c) => c.id === 'A').status, 'ready');
  assert.equal(r.scenes.queue({ lanes: [{ lane: 'bass', intent: 'start', clip: 'A' }] }).at, 104);
});
test('clip ids are per lane: the same id in two lanes is two clips, availability is by id', { skip }, () => {
  const r = rig();
  r.at(100.5);
  const q = r.scenes.queue({ lanes: [{ lane: 'drums', intent: 'start', clip: 'A' }, { lane: 'bass', intent: 'start', clip: 'A' }] });
  assert.equal(q.at, 100.5); // idle grid: the grid starts at the first launch (startLead 0 here)
  r.at(100.45); r.scenes.tick();
  assert.equal(h(r.log, 'drums', 'A').when, 100.5); assert.equal(h(r.log, 'bass', 'A').when, 100.5);
});
