import test from 'node:test';
import assert from 'node:assert/strict';
import { createInputState, keyIgnored, noteSource, keySource } from '../performance-input-model.mjs';

test('learn binds the next source to the waiting action and can be remapped', () => {
  const s = createInputState();
  assert.equal(s.learn(noteSource(36)), null);
  s.listen({ kind: 'clip', id: 'a' });
  assert.deepEqual(s.learn(noteSource(36)), { kind: 'clip', id: 'a' });
  s.listen({ kind: 'clip', id: 'a' }); s.learn(noteSource(40));
  assert.equal(s.lookup('note:36'), null);
  assert.equal(s.lookup('note:40').id, 'a');
  s.listen({ kind: 'clip', id: 'a' }); s.learn(keySource('KeyQ'));   // a key does not replace the pad
  assert.ok(s.lookup('note:40') && s.lookup('key:KeyQ'));
});

test('serialize round-trips and ignores bad data', () => {
  const s = createInputState(); s.listen({ kind: 'stop' }); s.learn('note:1');
  assert.equal(createInputState(s.serialize()).lookup('note:1').kind, 'stop');
  assert.equal(createInputState('{oops').list().length, 0);
});

test('keyboard focus and repeat rules', () => {
  assert.equal(keyIgnored({ repeat: true, target: {} }), true);
  assert.equal(keyIgnored({ target: { tagName: 'INPUT' } }), true);
  assert.equal(keyIgnored({ ctrlKey: true, target: {} }), true);
  assert.equal(keyIgnored({ target: { tagName: 'DIV' } }), false);
});
