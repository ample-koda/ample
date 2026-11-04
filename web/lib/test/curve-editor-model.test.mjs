import test from 'node:test';
import assert from 'node:assert/strict';
import { insertCurvePoint, moveCurvePoint, removeCurvePoint } from '../curve-editor-model.mjs';

const points = [{ x: 0, y: 10 }, { x: 40, y: 20 }, { x: 100, y: 80 }];

test('curve editing keeps ordered end anchors and separates neighboring points', () => {
  assert.deepEqual(moveCurvePoint(points, 0, 30, 37, 100, 100, 5, 8), [{ x: 0, y: 35 }, { x: 40, y: 20 }, { x: 100, y: 80 }]);
  assert.deepEqual(moveCurvePoint(points, 1, 100, 120, 100, 100, 5, 8), [{ x: 0, y: 10 }, { x: 90, y: 100 }, { x: 100, y: 80 }]);
});

test('curve points can be inserted between anchors and endpoints cannot be removed', () => {
  assert.deepEqual(insertCurvePoint(points, 60, 45, 100, 100, 5, 8), [{ x: 0, y: 10 }, { x: 40, y: 20 }, { x: 60, y: 45 }, { x: 100, y: 80 }]);
  assert.deepEqual(insertCurvePoint(points, 40, 45, 100, 100, 5, 8), points);
  assert.deepEqual(removeCurvePoint(points, 0), points);
  assert.deepEqual(removeCurvePoint(points, 1), [{ x: 0, y: 10 }, { x: 100, y: 80 }]);
});
