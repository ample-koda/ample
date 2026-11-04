import test from 'node:test';
import assert from 'node:assert/strict';
import { checkValue, defaultsOf } from '../../app/plugin-model.js';

test('defaults come from the schema', () => {
  assert.deepEqual(defaultsOf({ properties: { a: { default: 1 }, b: { type: 'string' }, c: { default: false } } }), { a: 1, c: false });
});

test('values are checked against the rule', () => {
  assert.equal(checkValue('n', 5, { type: 'integer', minimum: 1, maximum: 9 }), null);
  assert.match(checkValue('n', 0, { type: 'integer', minimum: 1 }), /at least 1/);
  assert.match(checkValue('n', 12, { type: 'integer', maximum: 9 }), /at most 9/);
  assert.match(checkValue('n', 1.5, { type: 'integer' }), /whole number/);
  assert.match(checkValue('s', 3, { type: 'string' }), /text/);
  assert.match(checkValue('e', 'x', { enum: ['a', 'b'] }), /one of a, b/);
});
