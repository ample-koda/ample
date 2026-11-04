import assert from 'node:assert/strict';
import test from 'node:test';
import { controlAt } from '../koda-control.mjs';

test('a general control is read as 0 to 1 on the timeline of the song', () => {
  const curve = { firstBar: 1, rate: 1000, unit: null, values: [0, 500, 1000, 1000] };
  assert.equal(controlAt(curve, 0, 2), 0);
  assert.equal(controlAt(curve, 0.001, 2), 0.5);
  assert.equal(controlAt(curve, 0.002, 2), 1);
  assert.equal(controlAt(curve, 5, 2), 0);   // after the curve
});

test('a curve that starts in a later bar is read from there, and a typed one is scaled to its own range', () => {
  const typed = { firstBar: 3, rate: 1000, unit: 'Hz', values: [300000, 2250000, 4200000] };
  assert.equal(controlAt(typed, 3.9, 2), 0);   // before bar 3 (4 s)
  assert.equal(controlAt(typed, 4.0, 2), 0);   // the low end
  assert.equal(controlAt(typed, 4.0021, 2), 1); // the high end
  assert.equal(controlAt(null, 1, 2), 0);
});
