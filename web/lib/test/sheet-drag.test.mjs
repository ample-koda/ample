import test from 'node:test';
import assert from 'node:assert/strict';
import { dragSheet, isTap, sheetMax } from '../sheet-drag.mjs';

test('dragging up grows the sheet up to the maximum', () => {
  assert.deepEqual(dragSheet({ startY: 500, y: 450, startHeight: 300, max: 600 }), { height: 350, close: false });
  assert.equal(dragSheet({ startY: 500, y: 0, startHeight: 300, max: 600 }).height, 600);
});

test('dragging down shrinks the sheet and closes it once it is clearly too small', () => {
  assert.deepEqual(dragSheet({ startY: 400, y: 450, startHeight: 300, max: 600 }), { height: 250, close: false });
  const r = dragSheet({ startY: 400, y: 640, startHeight: 300, max: 600 });
  assert.equal(r.close, true);
  assert.equal(r.height, 160);
});

test('a tiny movement is a tap; the maximum leaves room for the header', () => {
  assert.equal(isTap(100, 103), true);
  assert.equal(isTap(100, 140), false);
  assert.equal(sheetMax(844), 724);
  assert.equal(sheetMax(200), 160);
});
