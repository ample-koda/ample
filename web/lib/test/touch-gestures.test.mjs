import test from 'node:test';
import assert from 'node:assert/strict';
import { isLongPress, tapConnect, withinSlop } from '../touch-gestures.mjs';

test('slop: a small wobble is still a press, a drag is not', () => {
  assert.equal(withinSlop({ x: 0, y: 0 }, { x: 6, y: 6 }), true);
  assert.equal(withinSlop({ x: 0, y: 0 }, { x: 20, y: 0 }), false);
});

test('long press needs time and stillness', () => {
  const base = { start: { x: 5, y: 5 }, now: { x: 7, y: 6 }, downAt: 1000 };
  assert.equal(isLongPress({ ...base, at: 1200 }), false);
  assert.equal(isLongPress({ ...base, at: 1500 }), true);
  assert.equal(isLongPress({ ...base, now: { x: 60, y: 5 }, at: 2000 }), false);
});

test('tap-tap connect: output then input connects', () => {
  let r = tapConnect(null, { dir: 'out', node: 'a', idx: 0 });
  assert.equal(r.action, 'choose');
  r = tapConnect(r.state, { dir: 'in', node: 'b', idx: 1 });
  assert.equal(r.action, 'connect');
  assert.deepEqual(r.from, { node: 'a', idx: 0 });
  assert.deepEqual(r.to, { node: 'b', idx: 1 });
  assert.equal(r.state, null);
});

test('tap-tap connect: same output cancels, another output replaces, background cancels, input first asks for an output', () => {
  const chosen = { node: 'a', idx: 0 };
  assert.equal(tapConnect(chosen, { dir: 'out', node: 'a', idx: 0 }).action, 'cancel');
  const other = tapConnect(chosen, { dir: 'out', node: 'c', idx: 2 });
  assert.equal(other.action, 'choose');
  assert.deepEqual(other.state, { node: 'c', idx: 2 });
  assert.equal(tapConnect(chosen, { dir: null }).action, 'cancel');
  assert.equal(tapConnect(null, { dir: null }).action, 'none');
  assert.equal(tapConnect(null, { dir: 'in', node: 'b', idx: 0 }).action, 'need-output');
});

import { redirectToPort } from '../touch-gestures.mjs';
test('a tap that went down next to a dot but was moved to a block button goes back to the dot', () => {
  const button = { left: 100, right: 110, top: 50, bottom: 60 };
  const ports = [{ left: 120, right: 126, top: 54, bottom: 60 }, { left: 300, right: 306, top: 54, bottom: 60 }];
  assert.equal(redirectToPort({ x: 123, y: 57 }, button, ports), 0);      // on the dot
  assert.equal(redirectToPort({ x: 117, y: 57 }, button, ports), 0);      // beside the dot, outside the button
  assert.equal(redirectToPort({ x: 104, y: 55 }, button, ports), -1);     // on the button, far from a dot
  assert.equal(redirectToPort({ x: 95, y: 55 }, button, ports), -1);      // nowhere near a dot's reach
  assert.equal(redirectToPort({ x: 108, y: 57 }, button, [{ left: 114, right: 120, top: 54, bottom: 60 }]), -1);   // on the button, nearer to it
});
