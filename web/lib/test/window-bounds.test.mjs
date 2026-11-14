import test from 'node:test';
import assert from 'node:assert/strict';
import { keepInside } from '../window-bounds.mjs';

const view = { width: 1000, height: 700 };

test('a window that fits stays inside the viewport (issue 193)', () => {
  assert.deepEqual(keepInside({ left: 900, top: 600, width: 400, height: 300 }, view), { left: 600, top: 400 });
  assert.deepEqual(keepInside({ left: -50, top: -9, width: 400, height: 300 }, view), { left: 0, top: 0 });
  assert.deepEqual(keepInside({ left: 100, top: 100, width: 400, height: 300 }, view), { left: 100, top: 100 });
});

test('a window larger than the viewport sits at the corner', () => {
  assert.deepEqual(keepInside({ left: 300, top: 300, width: 1400, height: 900 }, view), { left: 0, top: 0 });
});

test('a bad position becomes 0', () => {
  assert.deepEqual(keepInside({ left: NaN, top: undefined, width: 400, height: 300 }, view), { left: 0, top: 0 });
});
