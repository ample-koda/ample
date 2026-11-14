import test from 'node:test';
import assert from 'node:assert/strict';
import { crossfadeGains, deckPhaseBeats, nextBarStart, phaseDriftBeats, routingPlan } from '../deck-math.mjs';

test('crossfader is equal power and hits the ends', () => {
  const a = crossfadeGains(0);
  assert.equal(a.a, 1);
  assert.ok(a.b < 1e-12);
  const m = crossfadeGains(0.5);
  assert.ok(Math.abs(m.a ** 2 + m.b ** 2 - 1) < 1e-12);
  assert.ok(crossfadeGains(9).a < 1e-12);
});

test('phase and bar start', () => {
  assert.equal(deckPhaseBeats(2, 120, 4), 0);
  assert.equal(deckPhaseBeats(0.5, 120, 4), 1);
  assert.equal(nextBarStart(1, 0, 120), 2);
  assert.equal(nextBarStart(2, 0, 120), 2);
  assert.equal(phaseDriftBeats(0, 3.5, 4), -0.5);
});

test('cue never routes to the room', () => {
  const cases = [
    null, { canSplit: false }, { canSplit: true }, { canSplit: true, cueDeviceId: 'x', roomDeviceId: 'x' },
    { canSplit: true, cueDeviceId: 'h', roomDeviceId: 'r' },
  ];
  for (const d of cases)
    assert.equal(routingPlan(d).cueToRoom, false);
  assert.equal(routingPlan({ canSplit: false }).mode, 'unavailable');
  assert.equal(routingPlan({ canSplit: true, cueDeviceId: 'h', roomDeviceId: 'r' }).mode, 'split');
});

test('crossfader curve: exact ends, middle, always unit power, bad input', () => {
  assert.deepEqual(crossfadeGains(0), { a: 1, b: 0 });
  assert.deepEqual(crossfadeGains(1), { a: 0, b: 1 });
  const m = crossfadeGains(0.5);
  assert.ok(Math.abs(m.a - Math.SQRT1_2) < 1e-12 && Math.abs(m.b - Math.SQRT1_2) < 1e-12);
  for (let i = 0; i <= 100; i++) { const g = crossfadeGains(i / 100); assert.ok(Math.abs(g.a ** 2 + g.b ** 2 - 1) < 1e-12); }
  assert.deepEqual(crossfadeGains(Infinity), { a: 0, b: 1 });
  assert.deepEqual(crossfadeGains(-Infinity), { a: 1, b: 0 });
  assert.ok(Math.abs(crossfadeGains(NaN).a - Math.SQRT1_2) < 1e-12);
});

test('phase and bar start with fractional tempo', () => {
  const bpm = 127.3, bar = 4 * 60 / bpm;
  assert.equal(nextBarStart(3 * bar, 0, bpm), 3 * bar);
  assert.ok(Math.abs(nextBarStart(3 * bar + 0.001, 0, bpm) - 4 * bar) < 1e-12);
  assert.equal(nextBarStart(-5, 2, bpm), 2); // before the clock started
  assert.equal(nextBarStart(0.1, 0, 90, 3), 3 * 60 / 90);
  assert.equal(deckPhaseBeats(4 * 60 / bpm * 5, bpm, 4), 0); // no 3.9999999 at the loop edge
  assert.ok(Math.abs(deckPhaseBeats(60 / bpm * 1.5, bpm, 4) - 1.5) < 1e-9);
  assert.equal(deckPhaseBeats(-0.5, 120, 4), 3);
  assert.throws(() => nextBarStart(1, 0, 120, 0), RangeError);
  assert.throws(() => nextBarStart(1, 0, NaN), RangeError);
  assert.equal(phaseDriftBeats(0, 4 - 1e-12, 4), 0);
  assert.throws(() => phaseDriftBeats(0, 1, 0), RangeError);
});

test('routing: default is the room device, a failure is reported, still never to the room', () => {
  assert.equal(routingPlan({ canSplit: true, cueDeviceId: 'default', roomDeviceId: null }).mode, 'same-device');
  assert.equal(routingPlan({ canSplit: true, cueDeviceId: 'h', roomDeviceId: null }).mode, 'split');
  const f = routingPlan({ canSplit: true, cueDeviceId: 'h', roomDeviceId: 'r', error: 'boom' });
  assert.equal(f.mode, 'failed'); assert.match(f.message, /boom/); assert.equal(f.cueToRoom, false);
});
