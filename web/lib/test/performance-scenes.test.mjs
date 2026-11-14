import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveScene, createSceneQueue } from '../../app/performance-scenes.js';

const scene = { id: 'chorus', name: 'Chorus', lanes: [
  { lane: 'drums', intent: 'start', clip: 'd2' },
  { lane: 'bass', intent: 'start', clip: 'b2' },
  { lane: 'vocal', intent: 'keep' },
  { lane: 'fx', intent: 'stop' }] };
const state = { available: c => c !== 'missing' };

function setup() {
  let t = 0; const launched = [];
  const q = createSceneQueue({ now: () => t, nextDownbeat: () => 4, launch: (at, b) => launched.push({ at, b }), leadSeconds: 0.1 });
  return { q, launched, setT: v => { t = v; } };
}

test('three lanes replace together in one batch with one timestamp', () => {
  const { q, launched, setT } = setup();
  assert.equal(q.queue(scene, state).ok, true);
  assert.equal(q.pending.scene.id, 'chorus');
  setT(3.85); assert.equal(q.tick(), false);
  setT(3.95); q.tick(); q.tick();
  assert.equal(launched.length, 1);
  assert.equal(launched[0].at, 4);
  assert.deepEqual(launched[0].b.map(x => x.action), ['start', 'start', 'keep', 'stop']);
});

test('missing clip keeps the current scene and names the problem', () => {
  const { q, launched } = setup();
  const r = q.queue({ lanes: [{ lane: 'drums', intent: 'start', clip: 'missing' }] }, state);
  assert.equal(r.ok, false); assert.equal(r.problems[0].lane, 'drums');
  assert.equal(q.pending, null); assert.equal(launched.length, 0);
});

test('cancel works just before the deadline, not after', () => {
  const a = setup(); a.q.queue(scene, state); a.setT(3.89);
  assert.equal(a.q.cancel(), true); a.setT(5); a.q.tick(); assert.equal(a.launched.length, 0);
  const b = setup(); b.q.queue(scene, state); b.setT(3.95);
  assert.equal(b.q.cancel(), false); b.q.tick(); assert.equal(b.launched.length, 1);
});

test('a second scene cannot replace one already handed to the transport; after the downbeat it can', () => {
  const { launched } = setup();
  let next = 4;
  const q2 = createSceneQueue({ now: () => t2, nextDownbeat: () => next, launch: (at, b) => launched.push({ at, b }) });
  let t2 = 0;
  q2.queue(scene, state); t2 = 3.95; q2.tick();
  const r = q2.queue(scene, state);
  assert.equal(r.ok, false); assert.match(r.problems[0].reason, /already scheduled/);
  t2 = 4.01; next = 8; assert.equal(q2.queue(scene, state).ok, true);
  assert.equal(launched.length, 1);
});
test('a lane twice in a scene, a missing available(), and a bad downbeat are refused', () => {
  const { q } = setup();
  const twice = { lanes: [{ lane: 'drums', intent: 'start', clip: 'd1' }, { lane: 'drums', intent: 'stop' }] };
  assert.equal(resolveScene(twice, state).ok, false);
  assert.throws(() => resolveScene(scene, {}), TypeError);
  const bad = createSceneQueue({ now: () => 0, nextDownbeat: () => NaN, launch() {} });
  assert.equal(bad.queue(scene, state).ok, false);
  const past = createSceneQueue({ now: () => 5, nextDownbeat: () => 4, launch() {} });
  assert.equal(past.queue(scene, state).ok, false);
  assert.equal(q.cancel(), false);
});
test('cancel exactly at the scheduling deadline is refused and the batch launches once', () => {
  const { q, launched, setT } = setup();
  q.queue(scene, state); setT(3.9);
  assert.equal(q.cancel(), false);
  q.tick(); q.tick();
  assert.equal(launched.length, 1);
});
test('a failing launch drops the pending scene and rethrows', () => {
  let t = 0; const q = createSceneQueue({ now: () => t, nextDownbeat: () => 4, launch() { throw new Error('transport'); } });
  q.queue(scene, state); t = 3.95;
  assert.throws(() => q.tick(), /transport/);
  assert.equal(q.pending, null);
});
test('what the queue hands out is a copy: changing it changes neither the pending scene nor the launched batch', () => {
  const { q, launched, setT } = setup();
  const mine = { id: 's', lanes: [{ lane: 'drums', intent: 'start', clip: 'd2' }] };
  const r = q.queue(mine, state);
  r.batch[0].clip = 'evil'; mine.lanes[0].clip = 'evil2'; q.pending.scene.lanes[0].clip = 'evil3';
  setT(3.95); q.tick();
  assert.equal(launched[0].b[0].clip, 'd2');
  assert.equal(q.pending.scene.lanes[0].clip, 'd2');
});
test('onChange fires on queue and cancel, a throwing listener does not break the queue, clear drops the pending scene', () => {
  const { q, setT } = setup();
  const seen = []; q.onChange(() => { throw new Error('x'); }); q.onChange((p) => seen.push(p && p.at));
  q.queue(scene, state); setT(1); q.cancel();
  assert.deepEqual(seen, [4, null]);
  q.queue(scene, state); setT(3.95); assert.equal(q.clear(), true); assert.equal(q.pending, null); assert.equal(q.clear(), false);
});
