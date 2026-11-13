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
  for (const d of [null, { canSplit: false }, { canSplit: true }, { canSplit: true, cueDeviceId: 'x', roomDeviceId: 'x' }, { canSplit: true, cueDeviceId: 'h', roomDeviceId: 'r' }])
    assert.equal(routingPlan(d).cueToRoom, false);
  assert.equal(routingPlan({ canSplit: false }).mode, 'unavailable');
  assert.equal(routingPlan({ canSplit: true, cueDeviceId: 'h', roomDeviceId: 'r' }).mode, 'split');
});
