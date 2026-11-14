import test from 'node:test';
import assert from 'node:assert/strict';
import { createLaunchScheduler } from '../../app/performance-oneshots.js';

// Voices are scheduled at launch with the future grid time; stop(at) / cancel() are recorded.
const make = () => {
  const log = [];
  const startVoice = (slot, when) => {
    const v = { slot, when, stops: [], cancelled: false, stop(at) { this.stops.push(at ?? 'now'); }, cancel() { this.cancelled = true; } };
    log.push(v); return v;
  };
  const s = createLaunchScheduler({ startVoice, quantize: (t) => Math.ceil(t) });
  return { s, log };
};

test('quantization applies to every mode, and the voice is scheduled at launch with the grid time', () => {
  for (const mode of ['loop', 'oneshot', 'momentary']) {
    const { s, log } = make(); s.configure('a', { mode, length: 2 });
    assert.deepEqual(s.launch('a', 0.5), { action: 'start', at: 1 });
    assert.equal(log[0].when, 1); assert.deepEqual(log[0].slot, { id: 'a', mode });
    assert.equal(s.state('a', 0.5).queued, true); assert.equal(s.state('a', 0.5).playing, false);
    assert.equal(s.state('a', 1).playing, true);
  }
});

test('one-shot length counts from the audible start; remaining() before and during', () => {
  const { s, log } = make(); s.configure('a', { mode: 'oneshot', length: 2 });
  s.launch('a', 0.5);                       // starts at 1, ends at 3
  assert.deepEqual(log[0].stops, [3]);
  assert.equal(s.remaining('a', 0.5), 2.5); // includes the wait for the start
  assert.equal(s.remaining('a', 2), 1);
  assert.equal(s.remaining('a', 3), null);  // finished
  assert.equal(s.state('a', 3).playing, false);
});

test('one-shot without a length is not given an end; a voice that ends itself is forgotten', () => {
  const { s, log } = make(); s.configure('a', { mode: 'oneshot' });
  s.launch('a', 0); assert.equal(s.remaining('a', 0), null); assert.deepEqual(log[0].stops, []);
  assert.equal(s.state('a', 5).playing, true);
  log[0].ended = true; assert.equal(s.state('a', 6).playing, false);
});

test('loop ignores the length; a second press stops it at the grid, not at once', () => {
  const { s, log } = make(); s.configure('bed', { mode: 'loop', length: 1 });
  s.launch('bed', 0); assert.deepEqual(log[0].stops, []);
  assert.equal(s.remaining('bed', 0.2), null);
  assert.deepEqual(s.launch('bed', 2.2), { action: 'stop', at: 3 });
  assert.deepEqual(log[0].stops, [3]);
  assert.equal(s.state('bed', 2.5).playing, true); assert.equal(s.state('bed', 3).playing, false);
});

test('a loop pad pressed again before its queued stop keeps playing when the handle can take the stop back', () => {
  const log = [];
  const startVoice = () => {
    const v = { stops: [], cancelStop() { this.stops.pop(); }, stop(at) { this.stops.push(at); } };
    log.push(v);
    return v;
  };
  const s = createLaunchScheduler({ quantize: (t) => Math.ceil(t), startVoice });
  s.configure('bed', { mode: 'loop' }); s.launch('bed', 0);
  assert.deepEqual(s.launch('bed', 2.2), { action: 'stop', at: 3 });
  assert.deepEqual(s.launch('bed', 2.4), { action: 'keep' });
  assert.deepEqual(log[0].stops, []); assert.equal(s.state('bed', 4).playing, true); assert.equal(log.length, 1);
});

test('retrigger replaces the queued start (no stacking) and the running voice stops at the new start', () => {
  const { s, log } = make(); s.configure('a', { mode: 'oneshot', length: 4 });
  s.launch('a', 0); s.launch('a', 1.2);            // 2nd press replaces nothing queued: first runs 0..4 but is cut at 2
  assert.deepEqual(log[0].stops, [4, 2]);
  s.launch('a', 1.5);                              // queued start at 2 is replaced by a new one at 2
  assert.equal(log[1].cancelled, true); assert.equal(log.length, 3);
  assert.equal(log[2].cancelled, false);
  assert.equal(s.state('a', 1.5).queued, true);
});

test('remaining() after a retrigger reports the new run, not the old one', () => {
  const { s } = make(); s.configure('a', { mode: 'oneshot', length: 4 });
  s.launch('a', 0);                                // plays 0..4
  assert.equal(s.remaining('a', 1.2), 2.8);
  s.launch('a', 1.2);                              // new run 2..6
  assert.equal(s.remaining('a', 1.2), 4.8);
  assert.equal(s.remaining('a', 3), 3);
});

test('momentary: a release before the queued start cancels it; nothing plays', () => {
  const { s, log } = make(); s.configure('m', { mode: 'momentary' });
  s.launch('m', 0.2); assert.equal(s.release('m', 0.6), true);
  assert.equal(log[0].cancelled, true); assert.deepEqual(log[0].stops, []);
  assert.equal(s.state('m', 5).playing, false); assert.equal(s.state('m', 5).queued, false);
});

test('momentary: a release exactly on the boundary cancels; just after it stops the voice there', () => {
  let r = make(); r.s.configure('m', { mode: 'momentary' });
  r.s.launch('m', 0.2); r.s.release('m', 1);
  assert.equal(r.log[0].cancelled, true);
  r = make(); r.s.configure('m', { mode: 'momentary' });
  r.s.launch('m', 0.2); r.s.release('m', 1.25);
  assert.equal(r.log[0].cancelled, false); assert.deepEqual(r.log[0].stops, [1.25]);
  assert.equal(r.s.state('m', 1.25).playing, false);
});

test('momentary: key repeat while held is ignored; a new press after release starts again', () => {
  const { s, log } = make(); s.configure('m', { mode: 'momentary' });
  s.launch('m', 1); assert.deepEqual(s.launch('m', 1.1), { action: 'held' }); assert.equal(log.length, 1);
  s.release('m', 1.5); assert.deepEqual(s.launch('m', 2.1), { action: 'start', at: 3 }); assert.equal(log.length, 2);
});

test('release does nothing to loops and one-shots', () => {
  const { s, log } = make(); s.configure('bed', { mode: 'loop' }); s.configure('o', { mode: 'oneshot', length: 8 });
  s.launch('bed', 0); s.launch('o', 0);
  assert.equal(s.release('bed', 1), false); assert.equal(s.release('o', 1), false);
  assert.deepEqual(log[0].stops, []); assert.deepEqual(log[1].stops, [8]);
});

test('disconnect stops only held momentary voices; stopAll stops everything and cancels queued starts', () => {
  const { s, log } = make();
  s.configure('bed', { mode: 'loop' }); s.configure('o', { mode: 'oneshot', length: 8 });
  s.configure('m', { mode: 'momentary' }); s.configure('m2', { mode: 'momentary' });
  s.launch('bed', 0); s.launch('o', 0); s.launch('m', 0); s.launch('m2', 0); s.release('m2', 0.5);
  s.disconnect(2);
  assert.deepEqual(log[0].stops, []);                 // bed untouched
  assert.deepEqual(log[1].stops, [8]);                // one-shot keeps its tail
  assert.deepEqual(log[2].stops, [2]);                // held momentary stopped
  assert.deepEqual(log[3].stops, [0.5]);             // already released: not touched again
  s.launch('bed', 2.5);                               // stops at 3 (toggle)
  s.configure('q', { mode: 'oneshot', length: 1 }); s.launch('q', 3.5);
  s.stopAll();
  assert.equal(log.at(-1).cancelled, true);           // queued start withdrawn
  assert.equal(log[1].stops.at(-1), 'now');           // playing voice stopped at once
  for (const id of ['bed', 'o', 'm', 'q']) { assert.equal(s.state(id).playing, false); assert.equal(s.state(id).queued, false); }
  assert.equal(s.remaining('o'), null);
});

test('a quantizer that returns a time in the past does not move the start before the press', () => {
  const log = [];
  const s = createLaunchScheduler({ startVoice: (slot, when) => { log.push(when); return { stop() {} }; }, quantize: () => NaN });
  s.configure('a', { mode: 'oneshot', length: 1 });
  assert.deepEqual(s.launch('a', 2), { action: 'start', at: 2 }); assert.deepEqual(log, [2]);
});

test('now() is the default clock (injected, seconds)', () => {
  let t = 10; const log = [];
  const s = createLaunchScheduler({
    now: () => t,
    quantize: (x) => Math.ceil(x / 2) * 2,
    startVoice: (slot, when) => { log.push(when); return { stop() {} }; },
  });
  s.configure('a', { mode: 'oneshot', length: 3 });
  t = 10.5; assert.equal(s.launch('a').at, 12);
  assert.equal(s.remaining('a'), 4.5); t = 13; assert.equal(s.remaining('a'), 2);
});
