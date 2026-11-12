import test from 'node:test';
import assert from 'node:assert/strict';
import { panGains, linearMix, additiveMix, softShape } from '../mix-laws.mjs';

// The integer table of PanTable.cs: ISqrt(t << 24) / 32768 with t = 0..64.
const isqrt = (n) => { let x = BigInt(Math.floor(Math.sqrt(n))); while (x * x > BigInt(n)) x--; while ((x + 1n) * (x + 1n) <= BigInt(n)) x++; return Number(x); };
const tableRight = (i) => isqrt(i * 2 ** 24) / 32768, tableLeft = (i) => isqrt((64 - i) * 2 ** 24) / 32768;

test('pan gains follow the server table at every position', () => {
  for (let pan = -32; pan <= 32; pan++) {
    const g = panGains(pan, false), i = pan + 32;
    assert.ok(Math.abs(g.left - tableLeft(i)) < 1e-3, `left ${pan}`);
    assert.ok(Math.abs(g.right - tableRight(i)) < 1e-3, `right ${pan}`);
  }
});

test('a stereo balance leaves the near side alone and is 1 at the centre', () => {
  assert.deepEqual(panGains(0, true), { left: 1, right: 1 });
  assert.equal(panGains(-10, true).left, 1);
  assert.equal(panGains(32, true).left, 0);
  assert.ok(panGains(16, true).left < 1);
});

test('mix laws: linear is not equal-power, additive keeps the dry sound', () => {
  assert.deepEqual(linearMix(50), { dry: 0.5, wet: 0.5 });
  assert.deepEqual(additiveMix(30), { dry: 1, wet: 0.3 });
  assert.equal(linearMix(150).wet, 1);
});

test('soft shaper is x/(1+|x|)', () => {
  assert.equal(softShape(1), 0.5);
  assert.equal(softShape(-3), -0.75);
});
