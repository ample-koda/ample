import test from 'node:test';
import assert from 'node:assert/strict';
import { GRID_KEYS, ROLL_KEYS, axisFactor, longPressAction, pinchStart, pinchView } from '../lite-touch.mjs';

const view = { pxBeat: 60, rowH: 16, scrollX: 120, scrollY: 400, gridRowH: 24, gridScrollY: 50 };

test('pinchStart gives the midpoint and the distances', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 200, y: 160 });
  assert.deepEqual(s.mid, { x: 150, y: 130 });
  assert.equal(s.dx, 100);
  assert.equal(s.dy, 60);
  assert.ok(Math.abs(s.dist - Math.hypot(100, 60)) < 1e-9);
});

test('spreading the fingers on x zooms time only, on y zooms rows only', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 200, y: 200 });
  const wide = pinchView(view, s, pinchStart({ x: 50, y: 100 }, { x: 250, y: 200 }));
  assert.equal(wide.pxBeat, 120);
  assert.equal(wide.rowH, 16);
  const tall = pinchView(view, s, pinchStart({ x: 100, y: 50 }, { x: 200, y: 250 }));
  assert.equal(tall.pxBeat, 60);
  assert.equal(tall.rowH, 32);
});

test('the beat and row under the start midpoint stay under the moving midpoint', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 200, y: 200 });
  const now = pinchStart({ x: 80, y: 120 }, { x: 280, y: 320 });
  const v = pinchView(view, s, now);
  assert.ok(Math.abs((now.mid.x + v.scrollX) / v.pxBeat - (s.mid.x + view.scrollX) / view.pxBeat) < 1e-9);
  assert.ok(Math.abs((now.mid.y + v.scrollY) / v.rowH - (s.mid.y + view.scrollY) / view.rowH) < 1e-9);
});

test('moving both fingers together without changing the distance only pans', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 200, y: 200 });
  const v = pinchView(view, s, pinchStart({ x: 70, y: 60 }, { x: 170, y: 160 }));
  assert.equal(v.pxBeat, 60);
  assert.equal(v.rowH, 16);
  assert.equal(v.scrollX, 150);
  assert.equal(v.scrollY, 440);
});

test('limits hold and the scroll never goes below 0', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 110, y: 110 });
  const v = pinchView(view, s, pinchStart({ x: 0, y: 0 }, { x: 1000, y: 1000 }));
  assert.ok(v.pxBeat <= 480 && v.rowH <= 40);
  const out = pinchView(view, pinchStart({ x: 0, y: 0 }, { x: 200, y: 200 }), pinchStart({ x: 400, y: 400 }, { x: 410, y: 410 }));
  assert.ok(out.scrollX >= 0 && out.scrollY >= 0 && out.pxBeat >= 6 && out.rowH >= 6);
});

test('aligned fingers do not zoom the other axis; very close fingers zoom by the whole distance', () => {
  assert.equal(axisFactor(4, 40, 100, 150), 1);
  assert.equal(axisFactor(4, 40, 20, 30), 1.5);
  assert.equal(axisFactor(100, 150, 100, 999), 1.5);
  assert.equal(axisFactor(0, 0, 0, 0), 1);
});

test('the Grid view zooms its own row size and scroll', () => {
  const s = pinchStart({ x: 100, y: 100 }, { x: 200, y: 200 });
  const v = pinchView(view, s, pinchStart({ x: 100, y: 50 }, { x: 200, y: 250 }), GRID_KEYS);
  assert.equal(v.gridRowH, 48);
  assert.equal(v.rowH, 16);
  assert.ok(v.gridScrollY >= 0);
  assert.equal(ROLL_KEYS.row, 'rowH');
});

test('long press: a note is deleted, a step is accented, empty ground undoes the draw', () => {
  assert.equal(longPressAction({ mode: 'roll', hitNote: true }), 'delete');
  assert.equal(longPressAction({ mode: 'roll', hitNote: false }), 'cancel');
  assert.equal(longPressAction({ mode: 'grid', hitNote: false }), 'accent');
});
