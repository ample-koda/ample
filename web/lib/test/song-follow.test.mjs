import test from 'node:test';
import assert from 'node:assert/strict';
import { clipTickAt } from '../song-follow.mjs';

const BAR = 4_000_000;   // 4 beats of 1,000,000 ticks

test('a clip without parts is there for the whole song from bar 1', () => {
  assert.equal(clipTickAt([], 1, BAR), 0);
  assert.equal(clipTickAt(undefined, 2.5, BAR), 1.5 * BAR);
  assert.equal(clipTickAt([], 0.5, BAR), null);
});

test('the position inside a clip counts from the start of its part', () => {
  const parts = [{ at: 9, length: 8 }];
  assert.equal(clipTickAt(parts, 9, BAR), 0);
  assert.equal(clipTickAt(parts, 11.25, BAR), 2.25 * BAR);
  assert.equal(clipTickAt(parts, 8.99, BAR), null);
  assert.equal(clipTickAt(parts, 17, BAR), null);   // the part ends before bar 17
});

test('a second part goes on where the first one ended', () => {
  const parts = [{ at: 17, length: 4 }, { at: 1, length: 4 }];   // the order does not matter
  assert.equal(clipTickAt(parts, 2, BAR), BAR);
  assert.equal(clipTickAt(parts, 18, BAR), 4 * BAR + BAR);
  assert.equal(clipTickAt(parts, 10, BAR), null);
});

test('nonsense gives null, never a wrong head', () => {
  assert.equal(clipTickAt([], NaN, BAR), null);
  assert.equal(clipTickAt([], 3, 0), null);
});
