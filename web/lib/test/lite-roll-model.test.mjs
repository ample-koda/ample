import test from 'node:test';
import assert from 'node:assert/strict';
import { TOOLBAR_ORDER, TOOLS, toolForKey, autoSnapBeats, snapBeats, snapTick, gridLevels, clipEndBars, clampToClip, hasHiddenData, normalizeLoop, hitLoop, dragLoop, velocityFromY, velocityRamp, nearestStart, litePlayRange, metronomeClicks, openCentre, scrollYForCentre, createCommitQueue, typingPitch, sliceNote } from '../lite-roll-model.mjs';
import { parse, format, addNote, updateNote, absolute } from '../notes-model.mjs';

const PPQ = 1000;

test('toolbar order and tool hotkeys follow the plan', () => {
  assert.deepEqual(TOOLBAR_ORDER, ['advanced', 'view', 'tools', 'typing', 'play', 'playSel', 'stop', 'metronome', 'loop', 'follow', 'snap', 'zoomOut', 'zoomIn', 'fit']);
  assert.deepEqual(TOOLS.map((tool) => tool.id), ['draw', 'paint', 'delete', 'slice', 'select', 'playback', 'zoom']);
  assert.equal(toolForKey('P'), 'draw');
  assert.equal(toolForKey('c'), 'slice');
  assert.equal(toolForKey('y'), 'playback');
  assert.equal(toolForKey('Z'), 'zoom');
  assert.equal(toolForKey('t'), null);   // the mute tool needs a text-format key (decision 22)
});

test('auto snap follows the zoom and fixed snaps do not', () => {
  assert.equal(autoSnapBeats(200), 0.125);
  assert.equal(autoSnapBeats(60), 0.25);
  assert.equal(autoSnapBeats(30), 0.5);
  assert.equal(autoSnapBeats(14), 1);
  assert.equal(autoSnapBeats(2), 4);
  assert.equal(snapBeats('auto', 60), 0.25);
  assert.equal(snapBeats('1/8', 400), 0.5);
  assert.equal(snapBeats('off', 400), 0);
});

test('snapTick rounds to the grid, Alt bypasses it', () => {
  assert.equal(snapTick(620, 0.5, PPQ), 500);
  assert.equal(snapTick(760, 0.5, PPQ), 1000);
  assert.equal(snapTick(620, 0.5, PPQ, true), 620);
  assert.equal(snapTick(620.4, 0, PPQ), 620);
});

test('grid lines appear only when 8 px apart and fade in', () => {
  assert.deepEqual(gridLevels(20).map((l) => l.kind + l.beats), ['bar4', 'beat1', 'step0.5']);
  const fine = gridLevels(64);
  assert.equal(fine.at(-1).beats, 0.125);
  assert.equal(fine.at(-1).alpha, 0);
  assert.equal(gridLevels(36).find((l) => l.beats === 0.5).alpha, 1);
  assert.ok(gridLevels(20).find((l) => l.beats === 0.5).alpha < 1);
  assert.equal(gridLevels(14).find((l) => l.beats === 0.5), undefined);
  assert.ok(gridLevels(14).find((l) => l.beats === 1).alpha < 1);
});

test('clip end never cuts the notes and can be extended', () => {
  const rows = [{ at: 0, length: 5 * PPQ }];
  assert.equal(clipEndBars(0, rows, PPQ), 2);
  assert.equal(clipEndBars(4, rows, PPQ), 4);
  assert.equal(clipEndBars(4, rows, PPQ, 2), 6);
  assert.equal(clipEndBars(0, [], PPQ), 1);
});

test('clampToClip keeps notes inside', () => {
  assert.deepEqual(clampToClip(3500, 2000, 4000), { start: 3500, length: 500 });
  assert.deepEqual(clampToClip(-5, 10, 4000), { start: 0, length: 10 });
  assert.deepEqual(clampToClip(9000, 10, 4000), { start: 3999, length: 1 });
  assert.equal(clampToClip(0, 10, 0), null);
});

test('hidden data marker', () => {
  assert.equal(hasHiddenData({ pitch: 60 }), false);
  assert.equal(hasHiddenData({ slide: 20 }), true);
  assert.equal(hasHiddenData({ port: 20 }), true);
  assert.equal(hasHiddenData({ pan: 20 }), false);
});

test('loop bracket gestures keep 0 <= start < end <= clip end', () => {
  const END = 8000, snap = (t) => Math.round(t / 500) * 500;
  assert.deepEqual(dragLoop('create', 1000, 3100, null, END, snap), { start: 1000, end: 3000 });
  assert.deepEqual(dragLoop('create', 3000, -800, null, END, snap), { start: 0, end: 3000 });
  assert.deepEqual(dragLoop('create', 3000, 99999, null, END, snap), { start: 3000, end: END });
  assert.equal(dragLoop('create', 1000, 1100, null, END, snap), null);
  const base = { start: 2000, end: 4000 };
  assert.deepEqual(dragLoop('start', 0, 2900, base, END, snap), { start: 3000, end: 4000 });
  assert.deepEqual(dragLoop('start', 0, 9000, base, END, snap), { start: 3999, end: 4000 });
  assert.deepEqual(dragLoop('end', 0, 100, base, END, snap), { start: 2000, end: 2001 });
  assert.deepEqual(dragLoop('end', 0, 20000, base, END, snap), { start: 2000, end: END });
  assert.deepEqual(dragLoop('move', 3000, 5000, base, END, snap), { start: 4000, end: 6000 });
  assert.deepEqual(dragLoop('move', 3000, 99999, base, END, snap), { start: 6000, end: 8000 });
  assert.deepEqual(dragLoop('move', 3000, -9999, base, END, snap), { start: 0, end: 2000 });
  assert.equal(normalizeLoop(5, 5, 100), null);
});

test('loop hit testing', () => {
  const loop = { start: 1000, end: 3000 };
  assert.equal(hitLoop(loop, 1050, 100), 'start');
  assert.equal(hitLoop(loop, 2950, 100), 'end');
  assert.equal(hitLoop(loop, 2000, 100), 'body');
  assert.equal(hitLoop(loop, 5000, 100), null);
  assert.equal(hitLoop(null, 5000, 100), null);
});

test('velocity strip maths', () => {
  assert.equal(velocityFromY(0, 100), 127);
  assert.equal(velocityFromY(100, 100), 1);
  assert.equal(velocityFromY(50, 100), 64);
  const rows = [{ index: 0, at: 0 }, { index: 1, at: 500 }, { index: 2, at: 1000 }, { index: 3, at: 5000 }];
  const ramp = velocityRamp(rows, 1000, 127, 0, 1);
  assert.deepEqual([...ramp.keys()], [0, 1, 2]);
  assert.equal(ramp.get(0), 1);
  assert.equal(ramp.get(2), 127);
  assert.equal(ramp.get(1), 64);
  assert.equal(nearestStart(rows, 480, 100).index, 1);
  assert.equal(nearestStart(rows, 3000, 100), null);
});

test('play range is driven by the playhead and the bracket, never the selection', () => {
  assert.deepEqual(litePlayRange(1500, false, null, 8000), { from: 1500, loopStart: 0, loopEnd: 0, end: 8000 });
  assert.deepEqual(litePlayRange(99999, false, null, 8000), { from: 0, loopStart: 0, loopEnd: 0, end: 8000 });
  assert.deepEqual(litePlayRange(1500, true, null, 8000), { from: 1500, loopStart: 0, loopEnd: 8000, end: 8000 });
  assert.deepEqual(litePlayRange(500, true, { start: 2000, end: 4000 }, 8000), { from: 2000, loopStart: 2000, loopEnd: 4000, end: 8000 });
  assert.deepEqual(litePlayRange(3000, true, { start: 2000, end: 4000 }, 8000), { from: 3000, loopStart: 2000, loopEnd: 4000, end: 8000 });
});

test('metronome clicks accent beat 1 and wrap at the loop end', () => {
  assert.deepEqual(metronomeClicks(0, 2500, PPQ), [{ offset: 0, accent: true }, { offset: 1000, accent: false }, { offset: 2000, accent: false }]);
  assert.deepEqual(metronomeClicks(500, 1000, PPQ).map((c) => c.offset), [500]);
  const wrapped = metronomeClicks(3500, 2000, PPQ, 0, 4000);
  assert.deepEqual(wrapped, [{ offset: 500, accent: true }, { offset: 1500, accent: false }]);
});

test('open centred on the notes, C4 to C5 when empty', () => {
  assert.deepEqual(openCentre([]), { low: 60, high: 72 });
  assert.deepEqual(openCentre([{ pitch: 40 }, { pitch: 52 }]), { low: 40, high: 52 });
  const y = scrollYForCentre(60, 72, 16, 320);
  assert.ok(y > 0 && y < 128 * 16 - 320);
  assert.equal(scrollYForCentre(0, 0, 16, 320), 128 * 16 - 320);
  assert.equal(scrollYForCentre(127, 127, 16, 320), 0);
});

test('commit queue: in order, no-op skipped, rollback on rejection', async () => {
  const written = [], rolled = [];
  let reject = false;
  const queue = createCommitQueue({ initial: 'a', write: async (text) => { written.push(text); return !reject; }, onRollback: (text) => rolled.push(text) });
  const results = await Promise.all([queue.enqueue('a'), queue.enqueue('b'), queue.enqueue('c')]);
  assert.deepEqual(results, ['unchanged', 'committed', 'committed']);
  assert.deepEqual(written, ['b', 'c']);
  reject = true;
  assert.equal(await queue.enqueue('d'), 'rolled-back');
  assert.deepEqual(rolled, ['c']);
  assert.equal(queue.committed, 'c');
  const thrower = createCommitQueue({ initial: '', write: async () => { throw new Error('x'); }, onRollback: (t) => rolled.push(`t:${t}`) });
  assert.equal(await thrower.enqueue('z'), 'rolled-back');
});

test('untouched entries and extension fields survive a Lite edit of another note', () => {
  const text = '1:0:480:60:96:0:0:0;pan=20 1:480:480:64:96;grp=2 1:960:480:67:100';
  let model = parse(text, PPQ);
  model = updateNote(model, 2, { velocity: 50 });
  const out = format(model);
  assert.ok(out.startsWith('1:0:480:60:96:0:0:0;pan=20 1:480:480:64:96;grp=2 '));
  model = addNote(model, { bar: 1, start: 1440, length: 480, pitch: 72, velocity: 96 });
  assert.ok(format(model).includes(';pan=20'));
  assert.ok(format(model).includes(';grp=2'));
});

test('typing keyboard maps two rows from the base octave', () => {
  assert.equal(typingPitch('z', 4), 60);
  assert.equal(typingPitch('s', 4), 61);
  assert.equal(typingPitch('m', 4), 71);
  assert.equal(typingPitch(',', 4), 72);
  assert.equal(typingPitch('/', 4), 74);
  assert.equal(typingPitch('q', 4), 72);
  assert.equal(typingPitch('U', 4), 83);
  assert.equal(typingPitch('1', 4), null);
  assert.equal(typingPitch('u', 10), null);
});

test('slice cuts one note in two, across a bar line too, keeping its fields', () => {
  const model = parse('1:0:1920:60:100;pan=20', PPQ);
  const row = absolute(model)[0];
  const cut = sliceNote(model, row, 480);
  const rows = absolute(cut);
  assert.equal(rows.length, 2);
  assert.deepEqual(rows.map((r) => [r.at, r.length, r.pitch, r.velocity]), [[0, 480, 60, 100], [480, 1440, 60, 100]]);
  assert.ok(format(cut).split(';pan=20').length === 3);
  assert.equal(sliceNote(model, row, 0), model);
  assert.equal(sliceNote(model, row, 99999), model);
  const long = parse('1:0:1500:60:100', 1000);
  const tall = sliceNote(long, absolute(long)[0], 1000);
  assert.equal(absolute(tall).length, 2);
});
