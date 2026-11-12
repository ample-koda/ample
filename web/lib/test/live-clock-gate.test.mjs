import test from 'node:test';
import assert from 'node:assert/strict';
import { createClockGate, duckStepEvents, gateStepEvents, planStep } from '../live-clock-gate.mjs';

test('duck: an x step dips to the floor and releases, other steps are open', () => {
  const [jump, ramp] = duckStepEvents('x...', 0, .125, 12, .1);
  assert.equal(jump.jump, true); assert.ok(Math.abs(jump.value - 10 ** (-12 / 20)) < 1e-9);
  assert.equal(ramp.dt, .1); assert.ok(Math.abs(ramp.value - 1) < 1e-9);
  assert.deepEqual(duckStepEvents('x...', 1, .125, 12, .1), [{ dt: 0, value: 1, jump: true }]);
  const [, partial] = duckStepEvents('x...', 0, .05, 20, .2);   // a release longer than the step is cut at the step end
  assert.equal(partial.dt, .05); assert.ok(partial.value < 1);
});

test('gate: fades 2 ms from the previous gain to this step', () => {
  const [a, b] = gateStepEvents('x.', 1, .1, .5);
  assert.equal(a.value, 1); assert.equal(b.value, .5); assert.equal(b.dt, .002);
  assert.equal(gateStepEvents('x.', 0, .1, .5)[0].value, .5);   // the step before step 0 is the last of the pattern
});

test('planStep: a running step starts at the interpolated value', () => {
  const events = [{ dt: 0, value: .1, jump: true }, { dt: 1, value: 1, jump: false }];
  assert.deepEqual(planStep(events, 10, 5), [{ op: 'set', value: .1, time: 10 }, { op: 'ramp', value: 1, time: 11 }]);
  const mid = planStep(events, 10, 10.5);
  assert.equal(mid[0].op, 'set'); assert.ok(Math.abs(mid[0].value - .55) < 1e-9); assert.deepEqual(mid[1], { op: 'ramp', value: 1, time: 11 });
  assert.deepEqual(planStep(events, 10, 12), [{ op: 'set', value: 1, time: 12 }]);
});

test('scheduler queues the steps ahead on the audio clock and wraps at the loop end', () => {
  const calls = []; let now = 0, position = 0.0;
  const param = { cancelScheduledValues() { calls.push(['cancel']); }, setValueAtTime: (v, t) => calls.push(['set', v, t]), linearRampToValueAtTime: (v, t) => calls.push(['ramp', v, t]) };
  const gate = createClockGate({ get currentTime() { return now; } }, param, {
    clock: () => ({ position, duration: .5, playing: true }), stepSeconds: () => .125, lookahead: .3, interval: 1e6,
    stepEvents: (k) => [{ dt: 0, value: k, jump: true }],
  });
  const sets = () => calls.filter((c) => c[0] === 'set').map((c) => [c[1], +c[2].toFixed(3)]);
  assert.deepEqual(sets(), [[0, 0], [1, .125], [2, .25]]);
  calls.length = 0; now = .25; position = .25; gate.tick();   // steps 3, then the loop wraps (0.5 s) to step 0 at 0.5
  assert.deepEqual(sets(), [[3, .375], [0, .5]]);
  calls.length = 0; now = .3; position = .0; gate.tick();     // the take jumped: replan from the new position
  assert.equal(calls[0][0], 'cancel'); assert.deepEqual(sets()[0], [0, .3]);
  gate.dispose();
});

test('with a tempo map the steps follow the beat grid across a tempo change (120 bpm until beat 2, then 60)', async () => {
  const { tempoMap } = await import('../audio-clock.mjs');
  const calls = []; let now = 0;
  const param = { cancelScheduledValues() {}, setValueAtTime: (v, t) => calls.push([v, +t.toFixed(3)]), linearRampToValueAtTime() {} };
  const map = tempoMap([{ beat: 0, bpm: 120 }, { beat: 2, bpm: 60 }]);   // beats start at 0, .5, 1, 2, 3 s
  const lengths = [];
  const gate = createClockGate({ get currentTime() { return now; } }, param, {
    clock: () => ({ position: 0, duration: 0, playing: true }), stepSeconds: () => .5, lookahead: 4.2, interval: 1e6, map, stepBeats: 1,
    stepEvents: (k, length) => { lengths.push([k, +length.toFixed(3)]); return [{ dt: 0, value: k, jump: true }]; },
  });
  assert.deepEqual(calls.slice(0, 5), [[0, 0], [1, .5], [2, 1], [3, 2], [4, 3]]);
  assert.deepEqual(lengths.slice(0, 4), [[0, .5], [1, .5], [2, 1], [3, 1]]);   // the step length is the one of that beat, not a constant
  gate.dispose();
});
