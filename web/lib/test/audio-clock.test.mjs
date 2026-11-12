import test from 'node:test';
import assert from 'node:assert/strict';
import { tempoMap, wrapLoop, makeAnchor, contextTimeOf, positionAt, stepsInWindow } from '../audio-clock.mjs';

const close = (a, b, e = 1e-9) => assert.ok(Math.abs(a - b) < e, `${a} vs ${b}`);

test('constant tempo: 120 bpm is half a second per beat', () => {
  const m = tempoMap([{ beat: 0, bpm: 120 }]);
  close(m.secondsAt(4), 2); close(m.beatAt(2), 4);
});
test('tempo change: seconds and beats round-trip across the change', () => {
  const m = tempoMap([{ beat: 0, bpm: 120 }, { beat: 4, bpm: 60 }]);
  close(m.secondsAt(4), 2); close(m.secondsAt(6), 4); close(m.beatAt(4), 6); close(m.bpmAt(5), 60);
  for (const b of [0, 1.5, 4, 7.25]) close(m.beatAt(m.secondsAt(b)), b);
});
test('empty or broken map falls back to one tempo', () => {
  close(tempoMap(null, 100).secondsAt(10), 6); close(tempoMap([{ beat: 2, bpm: 90 }]).secondsAt(3), 2);
});
test('loop wrap and take-to-context time', () => {
  const loop = { start: 0, end: 8 };
  close(wrapLoop(9.5, loop), 1.5); close(wrapLoop(-1, loop), 7); assert.equal(wrapLoop(3, null), 3);
  const a = makeAnchor(6, 10);   // take at 6 s heard at context 10 s
  close(contextTimeOf(a, 7, loop), 11); close(contextTimeOf(a, 2, loop), 14);   // next lap
  assert.equal(contextTimeOf(a, 2, null), null);
  close(positionAt(a, 14, loop), 2);
});
test('steps in a look-ahead window follow a tempo change', () => {
  const m = tempoMap([{ beat: 0, bpm: 120 }, { beat: 2, bpm: 60 }]);
  const a = makeAnchor(0, 100);
  const steps = stepsInWindow(m, a, 0, 1, 100, 3.5);   // beats at 100, 100.5, then 1 s per beat: 101, 102, 103
  assert.deepEqual(steps.map((s) => s.k), [0, 1, 2, 3, 4]);
  close(steps[1].time, 100.5); close(steps[2].time, 101); close(steps[3].time, 102); close(steps[4].time, 103);
  assert.deepEqual(stepsInWindow(m, a, 0, 0, 100, 3), []);
});
