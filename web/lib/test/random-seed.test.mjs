import test from 'node:test';
import assert from 'node:assert/strict';
import { randomSeed } from '../random-seed.mjs';

test('a random seed is a whole number that fits an unsigned 64-bit seed field', () => {
  for (let i = 0; i < 200; i++) {
    const seed = randomSeed();
    assert.match(seed, /^\d{1,10}$/);
    assert.ok(BigInt(seed) >= 1n && BigInt(seed) <= 4294967295n);
  }
});

test('it works without crypto and never returns zero', () => {
  assert.match(randomSeed(null), /^\d+$/);
  assert.equal(randomSeed({ getRandomValues: (words) => { words[0] = 0; } }), '1');
});
