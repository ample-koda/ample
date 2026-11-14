import test from 'node:test';
import assert from 'node:assert/strict';
import { addClip, addLane, addScene, emptyLayout, parseLayout, pruneLayout, removeClip, removeLane, removeScene, sceneChoice, serializeLayout, setSceneEntry } from '../performance-layout.mjs';
import { actionText, clipActionId, parseClipActionId } from '../performance-actions.mjs';

const base = () => {
  let l = emptyLayout();
  l = addLane(l, 'Drums').layout; l = addLane(l, 'Bass').layout;
  l = addClip(l, 'Drums', { id: 'kickA', label: 'Kick A' }).layout; l = addClip(l, 'Drums', { id: 'kickB', label: 'Kick B' }).layout;
  l = addClip(l, 'Bass', { id: 'bassA', label: 'Bass A' }).layout;
  return l;
};

test('a scene gets a name and an id that stays, and names are unique (issue 273)', () => {
  let l = addScene(base(), '  Chorus ').layout;
  assert.deepEqual(l.scenes, [{ id: 'scene-1', name: 'Chorus', lanes: [] }]);
  assert.equal(addScene(l, 'chorus').ok, false);
  assert.equal(addScene(l, '   ').ok, false);
  l = addScene(l, 'Verse').layout;
  assert.deepEqual(l.scenes.map((s) => s.id), ['scene-1', 'scene-2']);
  l = removeScene(l, 'scene-1');
  assert.equal(addScene(l, 'Bridge').layout.scenes.at(-1).id, 'scene-1', 'a free id is used again');
});

test('a scene says per lane: keep, stop or start a clip of that lane', () => {
  let l = addScene(base(), 'Chorus').layout;
  l = setSceneEntry(l, 'scene-1', 'Drums', 'start', 'kickB');
  l = setSceneEntry(l, 'scene-1', 'Bass', 'stop');
  assert.deepEqual(l.scenes[0].lanes, [{ lane: 'Drums', intent: 'start', clip: 'kickB' }, { lane: 'Bass', intent: 'stop' }]);
  assert.equal(sceneChoice(l.scenes[0], 'Drums'), 'start:kickB');
  assert.equal(sceneChoice(l.scenes[0], 'Bass'), 'stop');
  l = setSceneEntry(l, 'scene-1', 'Bass', 'keep');
  assert.equal(sceneChoice(l.scenes[0], 'Bass'), '');
});

test('a clip that is not on the lane, an unknown lane or scene change nothing', () => {
  const l = addScene(base(), 'Chorus').layout;
  assert.equal(setSceneEntry(l, 'scene-1', 'Drums', 'start', 'bassA'), l);
  assert.equal(setSceneEntry(l, 'scene-1', 'Nope', 'stop'), l);
  assert.equal(setSceneEntry(l, 'scene-9', 'Drums', 'stop'), l);
  assert.equal(setSceneEntry(l, 'scene-1', 'Drums', 'dance'), l);
});

test('removing a clip or a lane takes the scene entries that point at it, and pruning does too', () => {
  let l = setSceneEntry(addScene(base(), 'Chorus').layout, 'scene-1', 'Drums', 'start', 'kickB');
  l = setSceneEntry(l, 'scene-1', 'Bass', 'start', 'bassA');
  assert.deepEqual(removeClip(l, 'Drums', 'kickB').scenes[0].lanes.map((e) => e.lane), ['Bass']);
  assert.deepEqual(removeLane(l, 'Bass').scenes[0].lanes.map((e) => e.lane), ['Drums']);
  assert.deepEqual(pruneLayout(l, ['kickA', 'kickB']).layout.scenes[0].lanes.map((e) => e.lane), ['Drums']);
});

test('scenes survive serializing and parsing with their ids, and an older layout has none', () => {
  const l = setSceneEntry(addScene(addScene(base(), 'Chorus').layout, 'Verse').layout, 'scene-2', 'Drums', 'start', 'kickA');
  assert.deepEqual(parseLayout(serializeLayout(l)), l);
  assert.deepEqual(parseLayout({ lanes: [{ name: 'Drums', clips: [] }] }).scenes, []);
});

test('a clip action id holds the lane and the clip and is read back', () => {
  const id = clipActionId('Drums A', 'kickA');
  assert.deepEqual(parseClipActionId(id), { lane: 'Drums A', clip: 'kickA' });
  assert.equal(parseClipActionId('nothing'), null);
  assert.equal(parseClipActionId('\tx'), null);
});

test('the list of bindings names a clip, a scene and Stop, and says when one is gone', () => {
  const l = addScene(base(), 'Chorus').layout;
  assert.equal(actionText({ kind: 'stop' }, l), 'Stop all');
  assert.equal(actionText({ kind: 'scene', id: 'scene-1' }, l), 'Scene Chorus');
  assert.equal(actionText({ kind: 'scene', id: 'scene-9' }, l), 'A scene that is gone');
  assert.equal(actionText({ kind: 'clip', id: clipActionId('Drums', 'kickA') }, l), 'Drums: Kick A');
  assert.equal(actionText({ kind: 'clip', id: clipActionId('Drums', 'zzz') }, l), 'A clip that is gone');
});
