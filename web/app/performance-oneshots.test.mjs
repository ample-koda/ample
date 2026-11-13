import test from 'node:test';
import assert from 'node:assert/strict';
import { createLaunchScheduler } from './performance-oneshots.js';

const make = () => { const log = []; const s = createLaunchScheduler({ startVoice: (slot, when) => { const v = { slot, when, stopped: false, stop() { this.stopped = true; } }; log.push(v); return v; }, quantize: (t) => Math.ceil(t) }); return { s, log }; };

test('one-shot ends after its length and reports remaining', () => {
  const { s, log } = make(); s.configure('a', { mode: 'oneshot', length: 2 });
  s.launch('a', 0.5); assert.equal(s.state('a').queued, true); s.tick(1);
  assert.equal(s.remaining('a', 2), 1); s.tick(3); assert.equal(log[0].stopped, true); assert.equal(s.state('a').playing, false);
});
test('momentary: release before the queued start cancels it; release stops a playing voice', () => {
  const { s, log } = make(); s.configure('m', { mode: 'momentary' });
  s.launch('m', 0.2); s.release('m'); s.tick(5); assert.equal(log.length, 0);
  s.launch('m', 1); assert.equal(log.length, 1); s.release('m'); assert.equal(log[0].stopped, true);
});
test('rapid retriggers do not stack voices', () => {
  const { s, log } = make(); s.configure('a', { mode: 'oneshot', length: 4 });
  s.launch('a', 1); s.launch('a', 1); assert.equal(log.length, 2); assert.equal(log[0].stopped, true);
});
test('disconnect stops momentary only; stopAll stops everything', () => {
  const { s, log } = make(); s.configure('bed', { mode: 'loop' }); s.configure('m', { mode: 'momentary' });
  s.launch('bed', 1); s.launch('m', 1); s.disconnect();
  assert.equal(log[0].stopped, false); assert.equal(log[1].stopped, true);
  s.stopAll(); assert.equal(log[0].stopped, true); assert.equal(s.state('bed').playing, false);
});
