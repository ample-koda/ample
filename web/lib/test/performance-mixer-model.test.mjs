import test from 'node:test';
import assert from 'node:assert/strict';
import { createSession, muteGains, soloGains, quote } from '../performance-mixer-model.mjs';

test('a session move is read back, nothing is an edit until Apply is asked for', () => {
  const s = createSession();
  assert.equal(s.read('mix1', 'gains', '"0 0 0 0"'), '"0 0 0 0"');
  s.set('mix1', 'gains', '"-6 0 0 0"');
  assert.equal(s.read('mix1', 'gains', '"0 0 0 0"'), '"-6 0 0 0"');
  assert.equal(s.size, 1);
});

test('reset restores the saved mix', () => {
  const s = createSession(); s.set('mix1', 'pan', '"l c c c"'); s.reset();
  assert.equal(s.size, 0); assert.equal(s.read('mix1', 'pan', 'x'), 'x');
});

test('apply gives one batch of setParameter edits, the last value per parameter wins', () => {
  const s = createSession(); s.set('a', 'gains', '1'); s.set('a', 'gains', '2'); s.set('r', 'mix', '30 %');
  assert.deepEqual(s.applyEdits(), [{ type: 'setParameter', block: 'a', name: 'gains', value: '2' }, { type: 'setParameter', block: 'r', name: 'mix', value: '30 %' }]);
  s.applied(); assert.equal(s.size, 0);
});

test('mute and solo gains', () => {
  assert.equal(quote(muteGains(['0', '0', '0', '0'], 1)), '"0 -60 0 0"');
  assert.equal(quote(soloGains(['0', '-3', '0', '0'], 1)), '"-60 -3 -60 -60"');
});
