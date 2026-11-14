import test from 'node:test';
import assert from 'node:assert/strict';
import { createInputState, createInputRouter, keyIgnored, keyBindable } from '../performance-input-model.mjs';

const rig = (over = {}) => {
  const log = { launched: [], saved: 0, changed: 0 };
  const h = { isActive: true, devices: 0 };
  const state = createInputState(over.json);
  const router = createInputRouter({
    state, launch: (a) => log.launched.push(a), active: () => h.isActive, persist: () => log.saved++,
    onChange: () => log.changed++, deviceCount: () => h.devices,
  });
  return { state, router, log, h };
};
const key = (code, extra = {}) => ({
  code, target: { tagName: 'DIV' }, preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...extra,
});
const bindKey = (r, code, action) => { r.router.learnAction(action); return r.router.handleKey(key(code)); };

test('a bound key launches once, is consumed, and repeat/modifiers/text fields do not launch', () => {
  const r = rig(); bindKey(r, 'KeyQ', { kind: 'clip', id: 'a' });
  r.log.launched.length = 0;
  const e = key('KeyQ');
  assert.equal(r.router.handleKey(e), true); assert.ok(e.prevented && e.stopped);
  assert.deepEqual(r.log.launched, [{ kind: 'clip', id: 'a' }]);
  const blocked = [
    { repeat: true }, { ctrlKey: true }, { metaKey: true }, { altKey: true }, { shiftKey: true }, { isComposing: true },
    { defaultPrevented: true }, { target: { tagName: 'INPUT' } }, { target: { tagName: 'textarea' } },
    { target: { tagName: 'SELECT' } }, { target: { tagName: 'DIV', isContentEditable: true } },
  ];
  for (const bad of blocked)
    assert.equal(r.router.handleKey(key('KeyQ', bad)), false);
  assert.equal(r.log.launched.length, 1);
  assert.equal(r.router.handleKey(key('KeyW')), false); // unbound key is left alone
  assert.equal(keyIgnored({ target: null }), false);
});

test('nothing launches or learns while the workspace is not active', () => {
  const r = rig(); bindKey(r, 'KeyQ', { kind: 'scene', id: 's' }); r.log.launched.length = 0;
  r.h.isActive = false;
  assert.equal(r.router.handleKey(key('KeyQ')), false);
  r.state.bindings.set('note:36', { kind: 'stop' });
  assert.equal(r.router.handleNote('on', 36, 100), false);
  assert.equal(r.router.isMappedPad(36), false);
  r.router.learnAction({ kind: 'clip', id: 'z' });
  assert.equal(r.router.handleKey(key('KeyZ')), false);
  assert.equal(r.router.handleNote('on', 40, 100), false);
  assert.equal(r.state.lookup('key:KeyZ'), null);
  assert.equal(r.log.launched.length, 0);
});

test('note on launches; note off and note on with velocity 0 do not; bad pitch ignored', () => {
  const r = rig(); r.state.bindings.set('note:36', { kind: 'stop' });
  assert.equal(r.router.handleNote('off', 36, 0), false);
  assert.equal(r.router.handleNote('on', 36, 0), false);
  assert.equal(r.router.handleNote('on', 'x', 90), false);
  assert.equal(r.log.launched.length, 0);
  assert.equal(r.router.handleNote('on', 36, 90), true);
  assert.equal(r.router.handleNote('on', 36), true); // velocity not reported: still a press
  assert.equal(r.log.launched.length, 2);
  assert.equal(r.router.handleNote('on', 37, 90), false);
});

test('isMappedPad: true for a bound pad and for any pad while learning, only when active', () => {
  const r = rig(); r.state.bindings.set('note:36', { kind: 'stop' });
  assert.equal(r.router.isMappedPad(36), true);
  assert.equal(r.router.isMappedPad(37), false);
  r.router.learnAction({ kind: 'clip', id: 'a' });
  assert.equal(r.router.isMappedPad(37), true);
  r.h.isActive = false;
  assert.equal(r.router.isMappedPad(36), false);
});

test('learn: Escape cancels, reserved keys cannot be bound, a pad is learned too', () => {
  const r = rig();
  r.router.learnAction({ kind: 'clip', id: 'a' });
  for (const code of ['ShiftLeft', 'ControlRight', 'Tab', 'F5', 'CapsLock', 'MetaLeft']) {
    assert.equal(r.router.handleKey(key(code)), false);
    assert.ok(r.state.waiting, code);
  }
  assert.equal(keyBindable('Escape'), false); assert.equal(keyBindable('Space'), true);
  r.router.handleKey(key('Escape'));
  assert.equal(r.state.waiting, null);
  assert.equal(r.log.saved, 0);
  r.router.learnAction({ kind: 'clip', id: 'a' });
  assert.equal(r.router.handleNote('on', 50, 80), true);
  assert.equal(r.state.lookup('note:50').id, 'a'); assert.equal(r.log.saved, 1);
  assert.equal(r.router.handleNote('on', 51, 80), false); // learn is one shot
  assert.equal(r.log.launched.length, 0);
});

test('learn: two sources for one action = one per device type; remap and steal', () => {
  const r = rig(); const A = { kind: 'clip', id: 'a' }, B = { kind: 'clip', id: 'b' };
  r.router.learnAction(A); r.router.handleNote('on', 36, 1);
  r.router.learnAction(A); r.router.handleKey(key('KeyQ'));
  assert.equal(r.state.list().length, 2);
  r.router.learnAction(A); r.router.handleNote('on', 37, 1);
  assert.equal(r.state.lookup('note:36'), null); assert.ok(r.state.lookup('note:37'));
  r.router.learnAction(B); r.router.handleNote('on', 37, 1); // steals the pad from A
  assert.equal(r.state.lookup('note:37').id, 'b');
  assert.equal(r.state.list().filter(x => x.action.id === 'a').length, 1);
});

test('midi disconnect cancels a learn that had a device; without a device a state event does not', () => {
  const r = rig(); r.h.devices = 1;
  r.router.learnAction({ kind: 'stop' });
  r.router.handleMidiState(true, ['pad']);
  assert.ok(r.state.waiting);
  r.router.handleMidiState(true, []);
  assert.equal(r.state.waiting, null);
  const q = rig(); // key-only learn, no device ever: MIDI switching on/off must not cancel it
  q.router.learnAction({ kind: 'stop' });
  q.router.handleMidiState(false, []); q.router.handleMidiState(true, []);
  assert.ok(q.state.waiting);
  const s = rig(); s.h.devices = 1; s.router.learnAction({ kind: 'stop' });
  s.router.handleMidiState(false, ['stale']);
  assert.equal(s.state.waiting, null);
});

test('serialization: round trip, unknown and legacy entries survive but never launch, a late load does not override', () => {
  const json = JSON.stringify({ 'note:36': { kind: 'clip', id: 'a' }, 'key:KeyQ': { kind: 'stop' }, 'note:300': { kind: 'clip', id: 'x' },
    'pad:7': { kind: 'clip', id: 'p' }, 'note:40': { kind: 'future-thing', id: 'f' }, 'note:41': 'clip:legacy',
    'key:Tab': { kind: 'stop' }, 'note:42': { kind: 'clip' } });
  const s = createInputState(json);
  assert.deepEqual(s.list().map(x => x.source).sort(), ['key:KeyQ', 'note:36']);
  assert.equal(s.lookup('note:40'), null); assert.equal(s.lookup('note:300'), null); assert.equal(s.lookup('key:Tab'), null);
  const back = JSON.parse(s.serialize());
  assert.equal(Object.keys(back).length, 8);
  assert.equal(back['note:40'].kind, 'future-thing');
  assert.equal(back['note:41'], 'clip:legacy');
  assert.deepEqual(createInputState(s.serialize()).list(), s.list());
  assert.equal(createInputState('null').list().length, 0);
  assert.equal(createInputState('[1,2]').list().length, 0);
  assert.equal(createInputState('{oops').list().length, 0);
  // binding an extra's source replaces it; clear removes it
  s.listen({ kind: 'clip', id: 'n' }); s.learn('note:40');
  assert.equal(JSON.parse(s.serialize())['note:40'].kind, 'clip');
  assert.equal(s.clear('note:41'), true); assert.equal('note:41' in JSON.parse(s.serialize()), false);
  // a load that arrives after the user learned something does not override it
  const late = createInputState(); late.listen({ kind: 'clip', id: 'a' }); late.learn('note:50');
  late.merge(JSON.stringify({ 'note:36': { kind: 'clip', id: 'a' }, 'note:50': { kind: 'stop' }, 'key:KeyW': { kind: 'scene', id: 's' } }));
  assert.equal(late.lookup('note:50').id, 'a'); assert.equal(late.lookup('note:36'), null); assert.ok(late.lookup('key:KeyW'));
});

test('learn validates its input', () => {
  const s = createInputState();
  assert.equal(s.listen({ kind: 'nope' }), false); assert.equal(s.waiting, null);
  assert.equal(s.listen({ kind: 'clip' }), false);
  s.listen({ kind: 'stop', id: 'ignored' });
  assert.equal(s.learn('note:999'), null); assert.ok(s.waiting); // an impossible source keeps waiting
  assert.deepEqual(s.learn('note:036'), { kind: 'stop' }); assert.ok(s.lookup('note:36'));
});
