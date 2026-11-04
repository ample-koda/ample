import test from 'node:test';
import assert from 'node:assert/strict';
import { TINTS, tintOf } from '../tint.mjs';

test('the tint of a block is a known name, with or without quotes', () => {
  assert.equal(tintOf({ tint: 'purple' }), 'purple');
  assert.equal(tintOf({ tint: '"pink"' }), 'pink');
  assert.equal(tintOf({ tint: 'mauve' }), '');
  assert.equal(tintOf({}), '');
  assert.equal(tintOf(undefined), '');
  assert.equal(tintOf({ tint: 'constructor' }), '');
});

test('every tint is a colour', () => {
  for (const colour of Object.values(TINTS)) assert.match(colour, /^#[0-9a-f]{6}$/i);
});
