import test from 'node:test';
import assert from 'node:assert/strict';
import { planSessionSync } from '../performance-sync.mjs';

const lane = (name, ids, mode = 'loop') => ({ name, mode, clips: ids.map((id) => ({ id, label: id.toUpperCase() })) });

test('a layout that did not change needs nothing', () => {
  const layout = { lanes: [lane('Drums', ['kick']), lane('Bass', ['bass'])] };
  assert.deepEqual(planSessionSync(layout, layout), { addClips: [], removeClips: [], removedLanes: [], newLanes: [], modeChanged: [] });
});

test('new and removed clips, and clips that moved to another lane, are told apart by lane and id', () => {
  const before = { lanes: [lane('Drums', ['kick', 'snare']), lane('Bass', ['bass'])] };
  const after = { lanes: [lane('Drums', ['kick', 'hat']), lane('Bass', []), lane('Lead', ['bass'])] };
  const plan = planSessionSync(before, after);
  assert.deepEqual(plan.addClips.map((c) => `${c.lane}:${c.id}`), ['Drums:hat', 'Lead:bass']);
  assert.deepEqual(plan.removeClips.map((c) => `${c.lane}:${c.id}`), ['Drums:snare', 'Bass:bass']);
  assert.deepEqual(plan.newLanes, ['Lead']);
  assert.deepEqual(plan.removedLanes, []);
});

test('a removed lane is listed and so are its clips; a changed lane mode is told', () => {
  const before = { lanes: [lane('A', ['x']), lane('B', ['y'])] };
  const after = { lanes: [lane('B', ['y'], 'oneshot')] };
  const plan = planSessionSync(before, after);
  assert.deepEqual(plan.removedLanes, ['A']);
  assert.deepEqual(plan.removeClips.map((c) => c.id), ['x']);
  assert.deepEqual(plan.modeChanged, ['B']);
  assert.deepEqual(planSessionSync(undefined, after).addClips.length, 1);
});
