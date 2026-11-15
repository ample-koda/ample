import test from 'node:test';
import assert from 'node:assert/strict';
import {
  MAX_CLIPS_PER_LANE, MAX_LANES, addClip, addLane, clickAction, clipCount, clipOptions, emptyLayout, parseLayout, pruneLayout,
  removeClip, removeLane, serializeLayout, statusText,
} from '../performance-layout.mjs';

const withLane = (name = 'Drums') => addLane(emptyLayout(), name).layout;

test('a lane gets a clean name; empty, taken (any case) and too long names are refused', () => {
  assert.equal(addLane(emptyLayout(), '  Drums   A ').layout.lanes[0].name, 'Drums A');
  assert.equal(addLane(emptyLayout(), '   ').ok, false);
  assert.equal(addLane(withLane('Drums'), 'drums').ok, false);
  assert.equal(addLane(emptyLayout(), 'x'.repeat(33)).ok, false);
  assert.equal(addLane(emptyLayout(), 'x'.repeat(32)).ok, true);
});

test('a performance has at most the allowed number of lanes', () => {
  let layout = emptyLayout();
  for (let i = 0; i < MAX_LANES; i++) layout = addLane(layout, `Lane ${i}`).layout;
  const refused = addLane(layout, 'One more');
  assert.equal(refused.ok, false);
  assert.match(refused.reason, /at most 16 lanes/);
});

test('a clip is added to a lane once; unknown lanes, empty ids and a full lane are refused', () => {
  const layout = withLane();
  const added = addClip(layout, 'Drums', { id: 'kick', label: 'Kick loop' });
  assert.equal(added.ok, true);
  assert.deepEqual(added.layout.lanes[0].clips, [{ id: 'kick', label: 'Kick loop' }]);
  assert.equal(addClip(added.layout, 'Drums', { id: 'kick', label: 'Kick loop' }).ok, false);
  assert.equal(addClip(layout, 'Bass', { id: 'kick' }).ok, false);
  assert.equal(addClip(layout, 'Drums', { id: '  ' }).ok, false);
  let full = layout;
  for (let i = 0; i < MAX_CLIPS_PER_LANE; i++) full = addClip(full, 'Drums', { id: `b${i}` }).layout;
  assert.equal(addClip(full, 'Drums', { id: 'extra' }).ok, false);
});

test('a clip without a label is named after its block', () => {
  assert.equal(addClip(withLane(), 'Drums', { id: 'kick' }).layout.lanes[0].clips[0].label, 'kick');
});

test('no function changes the layout it was given', () => {
  const layout = addClip(withLane(), 'Drums', { id: 'kick', label: 'Kick' }).layout;
  const before = JSON.stringify(layout);
  addLane(layout, 'Bass');
  addClip(layout, 'Drums', { id: 'snare' });
  removeClip(layout, 'Drums', 'kick');
  removeLane(layout, 'Drums');
  pruneLayout(layout, []);
  assert.equal(JSON.stringify(layout), before);
});

test('removing a clip or a lane leaves the rest alone', () => {
  let layout = addClip(withLane(), 'Drums', { id: 'kick' }).layout;
  layout = addClip(layout, 'Drums', { id: 'snare' }).layout;
  layout = addLane(layout, 'Bass').layout;
  assert.deepEqual(removeClip(layout, 'Drums', 'kick').lanes[0].clips.map((c) => c.id), ['snare']);
  assert.deepEqual(removeLane(layout, 'Drums').lanes.map((l) => l.name), ['Bass']);
  assert.equal(removeClip(layout, 'Nope', 'kick').lanes.length, 2);
});

test('only blocks with a built take are offered, by title, each once', () => {
  const nodes = [{ id: 'b', title: 'Bass' }, { id: 'a', title: 'Arp' }, { id: 'c', title: 'No take' }, { id: 'a', title: 'Arp' }, {}];
  const options = clipOptions(nodes, { a: 'take-a', b: 'take-b' });
  assert.deepEqual(options.map((o) => o.id), ['a', 'b']);
  assert.equal(options[0].take, 'take-a');
  assert.deepEqual(clipOptions(undefined, undefined), []);
});

test('a block without a title is offered under its id', () => {
  assert.equal(clipOptions([{ id: 'kick' }], { kick: 't' })[0].label, 'kick');
});

test('pruning drops the clips of blocks that are gone and says which', () => {
  let layout = addClip(withLane(), 'Drums', { id: 'kick' }).layout;
  layout = addClip(layout, 'Drums', { id: 'snare' }).layout;
  const { layout: kept, dropped } = pruneLayout(layout, ['kick']);
  assert.deepEqual(kept.lanes[0].clips.map((c) => c.id), ['kick']);
  assert.deepEqual(dropped, [{ lane: 'Drums', id: 'snare' }]);
  assert.equal(clipCount(kept), 1);
});

test('a layout survives serializing and parsing, as an object and as text', () => {
  let layout = addClip(withLane(), 'Drums', { id: 'kick', label: 'Kick' }).layout;
  layout = addLane(layout, 'Bass').layout;
  const stored = serializeLayout(layout);
  assert.deepEqual(parseLayout(stored), layout);
  assert.deepEqual(parseLayout(JSON.stringify(stored)), layout);
});

test('a broken stored layout gives an empty one, and bad entries are skipped', () => {
  assert.deepEqual(parseLayout('not json'), emptyLayout());
  assert.deepEqual(parseLayout(null), emptyLayout());
  assert.deepEqual(parseLayout({ lanes: 'no' }), emptyLayout());
  const mixed = parseLayout({ lanes: [{ name: 'Drums', clips: [{ id: 'kick' }, { id: '' }, 7] }, { name: '' }, { name: 'drums' }] });
  assert.deepEqual(mixed.lanes.map((l) => l.name), ['Drums']);
  assert.deepEqual(mixed.lanes[0].clips.map((c) => c.id), ['kick']);
});

test('a click launches a ready clip, takes a queued one back and leaves a playing one alone', () => {
  assert.equal(clickAction('ready'), 'launch');
  assert.equal(clickAction('queued'), 'cancel');
  assert.equal(clickAction('playing'), 'none');
  assert.equal(clickAction(undefined), 'launch');
});

test('the status text names the three states and falls back to ready', () => {
  assert.deepEqual(['ready', 'queued', 'playing', 'x'].map(statusText), ['Ready', 'Queued', 'Playing', 'Ready']);
});

test('a lane has a mode that is kept when the layout is saved and read again, and an unknown mode is a loop', async () => {
  const { addLane, parseLayout, serializeLayout, setLaneMode, emptyLayout } = await import('../performance-layout.mjs');
  let layout = addLane(emptyLayout(), 'Drums').layout;
  assert.equal(layout.lanes[0].mode, 'loop');
  layout = addLane(layout, 'Accents').layout;
  layout = setLaneMode(layout, 'Accents', 'oneshot').layout;
  const back = parseLayout(serializeLayout(layout));
  assert.deepEqual(back.lanes.map((lane) => lane.mode), ['loop', 'oneshot']);
  assert.equal(setLaneMode(layout, 'Accents', 'sideways').ok, false);
  assert.equal(setLaneMode(layout, 'Missing', 'loop').ok, false);
  assert.equal(parseLayout({ lanes: [{ name: 'X', mode: 'banana', clips: [] }] }).lanes[0].mode, 'loop');
});

test('a pattern of a Sequencer block can be a clip: it is listed by block and pattern and kept while its block is in the song (issue 266)', async () => {
  const { addClip, addLane, emptyLayout, parsePatternClipId, patternClipId, patternOptions, pruneLayout } = await import('../performance-layout.mjs');
  const nodes = [{ id: 'drums-seq', tpl: 'sequencer', title: 'Drums' }, { id: 'bass', tpl: 'bass', title: 'Bass' }];
  const options = patternOptions(nodes, new Map([['drums-seq', ['verse', 'fill']], ['bass', ['not-a-sequencer']]]));
  assert.deepEqual(options.map((o) => [o.id, o.label]), [['pattern:drums-seq:fill', 'Drums · fill (pattern)'], ['pattern:drums-seq:verse', 'Drums · verse (pattern)']]);
  assert.deepEqual(parsePatternClipId(patternClipId('drums-seq', 'verse')), { block: 'drums-seq', pattern: 'verse' });
  assert.equal(parsePatternClipId('drums-seq'), null);
  let layout = addLane(emptyLayout(), 'Drums').layout;
  layout = addClip(layout, 'Drums', options[1]).layout;
  assert.equal(pruneLayout(layout, ['drums-seq']).layout.lanes[0].clips.length, 1);
  const gone = pruneLayout(layout, ['bass']);
  assert.equal(gone.layout.lanes[0].clips.length, 0);
  assert.deepEqual(gone.dropped, [{ lane: 'Drums', id: 'pattern:drums-seq:verse' }]);
});
