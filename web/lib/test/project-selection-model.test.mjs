import assert from 'node:assert/strict';
import { test } from 'node:test';
import { clickSelection, marqueeHits, planMoves } from '../project-selection-model.mjs';

const order = ['a', 'b', 'c', 'd', 'e'];

test('a plain click selects one item and makes it the anchor', () => {
  const r = clickSelection(order, new Set(['a', 'b']), 'a', 'd', {});
  assert.deepEqual([...r.selected], ['d']);
  assert.equal(r.anchor, 'd');
});

test('Ctrl+click toggles one item and moves the anchor', () => {
  let r = clickSelection(order, new Set(['a']), 'a', 'c', { ctrlKey: true });
  assert.deepEqual([...r.selected].sort(), ['a', 'c']);
  r = clickSelection(order, r.selected, r.anchor, 'a', { ctrlKey: true });
  assert.deepEqual([...r.selected], ['c']);
});

test('Shift+click selects the range from the anchor, in either direction, and keeps the anchor', () => {
  let r = clickSelection(order, new Set(['b']), 'b', 'd', { shiftKey: true });
  assert.deepEqual([...r.selected], ['b', 'c', 'd']);
  assert.equal(r.anchor, 'b');
  r = clickSelection(order, new Set(['d']), 'd', 'a', { shiftKey: true });
  assert.deepEqual([...r.selected].sort(), ['a', 'b', 'c', 'd']);
});

test('Ctrl+Shift+click adds the range to what is selected', () => {
  const r = clickSelection(order, new Set(['a']), 'c', 'e', { shiftKey: true, ctrlKey: true });
  assert.deepEqual([...r.selected].sort(), ['a', 'c', 'd', 'e']);
});

test('Shift+click without an anchor behaves like a plain click', () => {
  const r = clickSelection(order, new Set(), '', 'c', { shiftKey: true });
  assert.deepEqual([...r.selected], ['c']);
});

test('a rectangle selects the boxes it touches, whichever corner it was dragged from', () => {
  const boxes = [
    { path: 'a', left: 0, top: 0, right: 100, bottom: 100 },
    { path: 'b', left: 110, top: 0, right: 210, bottom: 100 },
    { path: 'c', left: 0, top: 110, right: 100, bottom: 210 },
  ];
  assert.deepEqual(marqueeHits({ left: 90, top: 90, right: 120, bottom: 120 }, boxes).sort(), ['a', 'b', 'c']);
  assert.deepEqual(marqueeHits({ left: 120, top: 40, right: 50, bottom: 20 }, boxes).sort(), ['a', 'b']);
  assert.deepEqual(marqueeHits({ left: 300, top: 300, right: 400, bottom: 400 }, boxes), []);
});

test('a move plan skips what is already there, what clashes, what is inside a selected folder and a folder into itself', () => {
  const sources = [
    { path: 'old/kick.wav' }, { path: 'old/snare.wav' }, { path: 'old', isDir: true }, { path: 'sounds/hat.wav' },
    { path: 'a/dup.wav' }, { path: 'b/dup.wav' },
  ];
  const { moves, skipped } = planMoves(sources, 'sounds', new Set(['hat.wav', 'snare.wav']));
  assert.deepEqual(moves.map((m) => `${m.from}>${m.to}`), ['old>sounds/old', 'a/dup.wav>sounds/dup.wav']);
  const reason = (path) => skipped.find((s) => s.path === path)?.reason;
  assert.match(reason('old/kick.wav'), /moves along with old/);
  assert.match(reason('sounds/hat.wav'), /already in that folder/);
  assert.match(reason('b/dup.wav'), /already exists/);
});

test('a folder cannot be moved into itself or into what is inside it', () => {
  const { moves, skipped } = planMoves([{ path: 'sounds', isDir: true }], 'sounds/drums');
  assert.deepEqual(moves, []);
  assert.match(skipped[0].reason, /itself/);
});

test('moving to the project root joins names without a leading slash', () => {
  const { moves } = planMoves([{ path: 'sounds/kick.wav' }], '');
  assert.deepEqual(moves.map((m) => m.to), ['kick.wav']);
});
