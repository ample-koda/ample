import test from 'node:test';
import assert from 'node:assert/strict';
import { focusKeyOf, findByFocusKey, neighbourKey } from '../focus-restore.mjs';

const control = (tagName, attrs = {}, dataset = {}) => ({
  tagName, type: attrs.type, id: attrs.id, disabled: attrs.disabled, dataset, getAttribute: (name) => attrs[name] ?? null,
});
const rootOf = (list) => ({ contains: (node) => list.includes(node), querySelectorAll: () => list });

test('the focus follows the control when another one appears before it', () => {
  const steps = [0, 1, 2].map((i) => control('INPUT', { type: 'number' }, { step: String(i) }));
  const before = rootOf([control('BUTTON', { id: 'sq-play' }), ...steps]);
  const key = focusKeyOf(before, steps[1]);
  const stepInputs = [0, 1, 2].map((i) => control('INPUT', { type: 'number' }, { step: String(i) }));
  const after = [control('BUTTON', { id: 'sq-play' }), control('BUTTON', { id: 'sq-new-hint' }), ...stepInputs];
  const found = findByFocusKey(rootOf(after), key);
  assert.equal(found.dataset.step, '1');   // an index would have landed on step 0
});

test('the focus does not move to a neighbour when its control is gone', () => {
  const a = control('INPUT', { id: 'a' }), b = control('INPUT', { id: 'b' }), c = control('INPUT', { id: 'c' });
  const key = focusKeyOf(rootOf([a, b, c]), b);
  assert.equal(findByFocusKey(rootOf([a, control('INPUT', { id: 'c' })]), key), null);
});

test('same-looking controls are told apart by their place among each other', () => {
  const rows = [0, 1, 2].map(() => control('SELECT', { 'aria-label': 'Voice' }));
  const key = focusKeyOf(rootOf(rows), rows[2]);
  const next = [control('BUTTON', { id: 'extra' }), ...[0, 1, 2].map(() => control('SELECT', { 'aria-label': 'Voice' }))];
  assert.equal(findByFocusKey(rootOf(next), key), next[3]);
});

test('disabled and tabindex -1 controls are neither remembered nor found', () => {
  const off = control('BUTTON', { id: 'x', disabled: true }), skip = control('DIV', { id: 'y', tabindex: '-1' });
  assert.equal(focusKeyOf(rootOf([off, skip]), off), null);
  assert.equal(focusKeyOf(rootOf([off, skip]), skip), null);
  const live = control('BUTTON', { id: 'x' }), key = focusKeyOf(rootOf([live]), live);
  assert.equal(findByFocusKey(rootOf([off]), key), null);
});

test('a key for nothing finds nothing', () => {
  assert.equal(findByFocusKey(rootOf([control('BUTTON', {})]), null), null);
  assert.equal(focusKeyOf(rootOf([]), null), null);
});

test('the neighbour key is the control that Tab or Shift+Tab would reach', () => {
  const [a, b, c] = ['a', 'b', 'c'].map((id) => control('INPUT', { id }));
  const skip = control('INPUT', { id: 'z', disabled: true });
  const root = rootOf([a, skip, b, c]);
  assert.equal(findByFocusKey(root, neighbourKey(root, a, 1)), b);   // the disabled one is not a stop
  assert.equal(findByFocusKey(root, neighbourKey(root, c, -1)), b);
  assert.equal(neighbourKey(root, c, 1), null);
  assert.equal(neighbourKey(root, control('INPUT', { id: 'elsewhere' }), 1), null);
});

test('a step that changes its value or title with the edit is still found', () => {
  const cell = (value, title) => control('BUTTON', { title }, { bar: '0', step: '1', value });
  const old = cell('.', 'rest');
  const key = focusKeyOf(rootOf([control('BUTTON', {}, { bar: '0', step: '0', value: 'x' }), old]), old);
  const fresh = [control('BUTTON', {}, { bar: '0', step: '0', value: 'x' }), cell('x', 'hit')];
  assert.equal(findByFocusKey(rootOf(fresh), key), fresh[1]);
});
