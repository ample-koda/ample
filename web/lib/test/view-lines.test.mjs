import test from 'node:test';
import assert from 'node:assert/strict';
import { parseView, writeView } from '../view-lines.mjs';

const song = 'composition "x" { ample 1  tempo 100  master-seed 1 }\ndrums a at 1 for 2 {\n}\nlayout a 10 20\n';
const groups = { g1: { id: 'g1', title: 'Group "1"', members: ['a', 'b c'], x: 5, y: 6, labels: { 'a:0': 'Beat' } } };

test('groups, sizes and pins are written as a view block at the end and read back the same', () => {
  const text = writeView(song, groups, { a: 0.8, 'my block': 1.25 }, ['kick', 'a b']);
  assert.ok(text.startsWith(song.trimEnd()));
  assert.equal(text.slice(song.length), '\nview {\n  group g1 {\n    title "Group \\"1\\""\n    member a\n    member "b c"\n    x 5\n    y 6\n    label "a:0" "Beat"\n  }\n  size a 0.8\n  size "my block" 1.25\n  pin "a b"\n  pin kick\n}\n');
  const back = parseView(text);
  assert.deepEqual(back.groups, groups);
  assert.deepEqual(back.sizes, { a: 0.8, 'my block': 1.25 });
  assert.deepEqual(back.pins.sort(), ['a b', 'kick']);
  assert.equal(back.found, true);
});

test('writing again replaces the old block, and nothing is left when there is nothing to keep', () => {
  const once = writeView(song, groups, { a: 0.8 });
  const twice = writeView(once, groups, { a: 0.8 });
  assert.equal(twice, once);
  const none = writeView(once, {}, {});
  assert.equal(none, song);
  assert.equal(parseView(none).found, false);
});

test('a line that was edited by hand is skipped, and a size of 1 is not written', () => {
  const text = `${song}\nview {\n  group g {\n    member a\n    x nope\n  }\n  size a nope\n  size b 0.5\n  group broken\n}\n`;
  const back = parseView(text);
  assert.deepEqual(back.groups.g.members, ['a']);
  assert.deepEqual(back.sizes, { b: 0.5 });
  assert.doesNotMatch(writeView(song, {}, { a: 1 }), /size/);
});

test('the older comment lines are still read, and the next write turns them into the block', () => {
  const old = `${song}\n# view: the groups, block sizes and pinned blocks of the canvas (kept by AMPLE; the song does not depend on them)\n# view group ${JSON.stringify(groups.g1)}\n# view size my block 1.25\n# view pin kick\n# view group {broken\n`;
  const back = parseView(old);
  assert.deepEqual(back.groups, groups);
  assert.deepEqual(back.sizes, { 'my block': 1.25 });
  assert.deepEqual(back.pins, ['kick']);
  const upgraded = writeView(old, back.groups, back.sizes, back.pins);
  assert.doesNotMatch(upgraded, /# view/);
  assert.deepEqual(parseView(upgraded), back);
});
