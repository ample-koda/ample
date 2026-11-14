import test from 'node:test';
import assert from 'node:assert/strict';
import { seedOfText, withSeed } from '../seed-line.mjs';

const plain = 'composition "Plain" { ample 1 tempo 100 master-seed 7 }\nblock x {}\n';
const waltz = 'composition "Waltz" { ample 1 tempo 96 meter 3/4 master-seed 7 }\nblock x {}\n';

test('the seed is found in a header without a meter', () => {
  assert.equal(seedOfText(plain), '7');
});

test('the seed is found in a header with a meter clause (issue 88)', () => {
  assert.equal(seedOfText(waltz), '7');
  assert.equal(seedOfText('composition "Six" { ample 1 tempo 120.5 meter 6/8 master-seed 12 }'), '12');
});

test('changing the seed keeps the meter and the rest of the text', () => {
  assert.equal(withSeed(waltz, '42'), 'composition "Waltz" { ample 1 tempo 96 meter 3/4 master-seed 42 }\nblock x {}\n');
  assert.equal(withSeed(plain, '9'), 'composition "Plain" { ample 1 tempo 100 master-seed 9 }\nblock x {}\n');
});

test('a seed larger than the safe integers is kept digit for digit', () => {
  const big = '18446744073709551615';
  assert.equal(seedOfText(withSeed(waltz, big)), big);
  assert.ok(withSeed(waltz, big).includes('meter 3/4 master-seed ' + big));
});

test('older headers (project, no ample clause) are still found', () => {
  assert.equal(seedOfText('project "Old" { tempo 90 master-seed 3 }'), '3');
});

test('text without a seed in this shape is not changed', () => {
  const text = 'composition "No" { ample 1 tempo 100 }\n';
  assert.equal(seedOfText(text), null);
  assert.equal(withSeed(text, '5'), text);
  assert.equal(seedOfText(undefined), null);
});
