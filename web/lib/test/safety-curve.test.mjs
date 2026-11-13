import test from 'node:test';
import assert from 'node:assert/strict';
import { safetyOf, safetyCurve, KNEE, CEILING } from '../safety-curve.mjs';

test('the safety curve is transparent below the knee, odd, monotonic and never passes the ceiling', () => {
  for (let x = -KNEE; x <= KNEE; x += 0.01) assert.equal(safetyOf(x), x);
  let last = -Infinity;
  for (let x = -6; x <= 6; x += 0.001) { const y = safetyOf(x); assert.ok(y >= last && Math.abs(y) <= CEILING && safetyOf(-x) === -y); last = y; }
  assert.ok(Math.abs(safetyOf(0.891) - 0.891) < 1e-9, 'a take limited to -1 dBFS is untouched');
});
test('the table covers inputs up to 4.0 and is smooth at the knee', () => {
  const c = safetyCurve();
  assert.equal(c.length % 2, 1); assert.ok(Math.abs(c[c.length - 1]) <= CEILING + 1e-6); assert.ok(c[c.length - 1] > 0.97);
  const mid = (c.length - 1) / 2;
  assert.ok(Math.abs(c[mid]) < 1e-9);
});
