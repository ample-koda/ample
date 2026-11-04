import test from 'node:test';
import assert from 'node:assert/strict';
import { projectRowsWindow, projectViewFromZoom } from '../project-panel-model.mjs';

test('a negative zoom is the detailed list and zero or more is a tile size (clamped to 4)', () => {
  assert.deepEqual(projectViewFromZoom(-1, 'tiles', 126, 100, 26), { view: 'details', size: 0 });
  assert.deepEqual(projectViewFromZoom(0, 'details', null, 100, 26), { view: 'tiles', size: 0 });
  assert.deepEqual(projectViewFromZoom(2, 'details', 0, 100, 26), { view: 'tiles', size: 2 });
  assert.deepEqual(projectViewFromZoom(9, 'tiles', 0, 100, 26), { view: 'tiles', size: 4 });
});

test('large project lists render only the visible row window with bounded overscan', () => {
  assert.deepEqual(projectRowsWindow(1800, 0, 320, 32, 8, 2000), { start: 0, end: 1800 });
  assert.deepEqual(projectRowsWindow(10000, 32000, 320, 32, 8, 2000), { start: 992, end: 1018 });
  assert.deepEqual(projectRowsWindow(10000, 0, 320, 32, 8, 2000), { start: 0, end: 26 });
});
