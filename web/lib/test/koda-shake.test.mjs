import test from 'node:test';
import assert from 'node:assert/strict';
import { makeShake } from '../koda-shake.mjs';

const run = (points) => { const s = makeShake(); let hit = false; points.forEach(([x, y], i) => { hit = s.feed(x, y, i * 40) || hit; }); return hit; };

test('a quick back and forth is a shake', () => {
  assert.equal(run([[0, 0], [40, 0], [0, 0], [40, 0], [0, 0], [40, 0], [0, 0]]), true);
  assert.equal(run([[0, 0], [0, 40], [0, 0], [0, 40], [0, 0], [0, 40], [0, 0]]), true);
});

test('a steady drag, a single turn or a tremble is not', () => {
  assert.equal(run([[0, 0], [30, 0], [60, 0], [90, 0], [120, 0]]), false);
  assert.equal(run([[0, 0], [60, 0], [0, 0], [60, 0]]), false);
  assert.equal(run([[0, 0], [4, 0], [0, 0], [4, 0], [0, 0], [4, 0], [0, 0], [4, 0], [0, 0]]), false);
});

test('slow turns spread over more than 700 ms are not a shake', () => {
  const s = makeShake(); let hit = false;
  [0, 40, 0, 40, 0, 40, 0].forEach((x, i) => { hit = s.feed(x, 0, i * 400) || hit; });
  assert.equal(hit, false);
});
