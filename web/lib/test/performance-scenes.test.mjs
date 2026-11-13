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
