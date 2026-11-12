import test from 'node:test';
import assert from 'node:assert/strict';
import { createLearnState, ccToNorm, ccLabel } from '../midi-learn-model.mjs';

test('a waiting target takes the next controller, which then finds it again', () => {
  const state = createLearnState();
  assert.equal(state.hear(1, 21), null);   // nothing waits: a loose controller learns nothing
  state.listen({ block: 'tone', name: 'high' });
  assert.deepEqual(state.hear(1, 21), { block: 'tone', name: 'high' });
  assert.equal(state.waiting, null);
  assert.deepEqual(state.lookup(1, 21), { block: 'tone', name: 'high' });
  assert.equal(state.lookup(1, 22), null);
});

test('one controller per target and one target per controller', () => {
  const state = createLearnState();
  state.listen({ block: 'a', name: 'x' }); state.hear(1, 1);
  state.listen({ block: 'a', name: 'x' }); state.hear(1, 2);   // the knob moves to another controller
  assert.equal(state.lookup(1, 1), null);
  assert.equal(state.controllerOf({ block: 'a', name: 'x' }), '1:2');
  state.listen({ block: 'b', name: 'y' }); state.hear(1, 2);   // another knob takes the controller
  assert.equal(state.controllerOf({ block: 'a', name: 'x' }), null);
});

test('clear forgets, and the state survives a save and a load', () => {
  const state = createLearnState();
  state.listen({ block: 'a', name: 'x' }); state.hear(2, 7);
  const copy = createLearnState(state.serialize());
  assert.deepEqual(copy.lookup(2, 7), { block: 'a', name: 'x' });
  copy.clear({ block: 'a', name: 'x' });
  assert.equal(copy.lookup(2, 7), null);
  assert.equal(createLearnState('not json').bindings.size, 0);
});

test('values and labels', () => {
  assert.equal(ccToNorm(0), 0); assert.equal(ccToNorm(127), 1); assert.ok(Math.abs(ccToNorm(64) - 64 / 127) < 1e-12);
  assert.equal(ccLabel('1:21'), 'CC 21'); assert.equal(ccLabel('3:21'), 'CC 21 ch 3');
});
