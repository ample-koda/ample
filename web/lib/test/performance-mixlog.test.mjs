import test from 'node:test';
import assert from 'node:assert/strict';
import { OPEN, autoText, createMixLog, laneCurves, moved, unitsOf } from '../performance-mixlog.mjs';

const settings = (lanes) => new Map(Object.entries(lanes));
const flat = { gain: 1, pan: 0, cutoff: null };

test('the log keeps a snapshot only when something changed', () => {
  const log = createMixLog();
  assert.equal(log.record(0, settings({ A: flat })), true);
  assert.equal(log.record(1, settings({ A: flat })), false);
  assert.equal(log.record(2, settings({ A: { ...flat, gain: 0.5 } })), true);
  assert.equal(log.snapshots().length, 2);
  log.reset();
  assert.equal(log.snapshots().length, 0);
});

test('the values are in the units of the blocks of the saved song', () => {
  assert.deepEqual(unitsOf({ gain: 1, pan: 0, cutoff: null }), { gain: 0, pan: 0, cutoff: OPEN });
  assert.equal(unitsOf({ gain: 0, pan: 0, cutoff: null }).gain, -60);
  assert.ok(Math.abs(unitsOf({ gain: 0.5, pan: 0, cutoff: null }).gain + 6.02) < 0.01);
  assert.equal(unitsOf({ gain: 1, pan: -1, cutoff: 800 }).pan, -32);
  assert.equal(unitsOf({ gain: 1, pan: 0.5, cutoff: 800 }).cutoff, 800);
});

test('a lane that did not move has constants, and a neutral one is not "moved"', () => {
  const log = createMixLog();
  log.record(0, settings({ A: flat }));
  const curves = laneCurves(log.snapshots(), 'A');
  assert.deepEqual(curves, { gain: { value: 0 }, pan: { value: 0 }, cutoff: { value: OPEN } });
  assert.equal(moved(curves), false);
  const kept = createMixLog(); kept.record(0, settings({ A: { gain: 0.5, pan: 0.25, cutoff: 1000 } }));
  const c = laneCurves(kept.snapshots(), 'A');
  assert.equal(moved(c), true);
  assert.deepEqual([c.pan.value, c.cutoff.value], [8, 1000]);
});

test('a filter sweep becomes a curve and a long drag is thinned to what it needs', () => {
  const log = createMixLog();
  log.record(0, settings({ A: flat }));
  // from beat 8 to beat 16 (bars 3 to 5 at four beats a bar) the cutoff falls from open to 400 Hz, in 80 small moves
  for (let k = 0; k <= 80; k++) log.record(8 + k * 0.1, settings({ A: { gain: 1, pan: 0, cutoff: Math.round(2 ** (Math.log2(OPEN) - (Math.log2(OPEN) - Math.log2(400)) * (k / 80))) } }));
  const curve = laneCurves(log.snapshots(), 'A').cutoff;
  assert.ok(curve.points && curve.points.length < 12, `thinned to ${curve.points?.length}`);
  assert.equal(curve.points[0][0], 1);
  assert.equal(curve.points.at(-1)[1], 400);
  assert.ok(Math.abs(curve.points.at(-1)[0] - 5) < 0.01, 'the end of the move is at bar 5');
});

test('a mute is a step: the old value is held until just before the new one', () => {
  const log = createMixLog();
  log.record(0, settings({ A: flat }));
  log.record(16, settings({ A: { gain: 0, pan: 0, cutoff: null } }));   // bar 5
  log.record(24, settings({ A: flat }));   // bar 7
  assert.deepEqual(laneCurves(log.snapshots(), 'A').gain.points, [[1, 0], [4.96, 0], [5, -60], [6.96, -60], [7, 0]]);
});

test('the first launch is bar 1: what was set before it is the value at bar 1', () => {
  const log = createMixLog();
  log.record(0, settings({ A: { gain: 0.5, pan: 0, cutoff: null } }));   // before the first launch
  log.record(10, settings({ A: { gain: 1, pan: 0, cutoff: null } }));
  const gain = laneCurves(log.snapshots(), 'A', { shiftBeats: 8, beatsPerBar: 4 }).gain;
  assert.equal(gain.points[0][0], 1);
  assert.ok(Math.abs(gain.points[0][1] + 6) < 0.1, 'it started at -6 dB');
  assert.ok(Math.abs(gain.points.at(-1)[0] - 1.5) < 0.01 && gain.points.at(-1)[1] === 0);
});

test('a lane that is not in the log is neutral, and the text of a curve is auto(bar:value, ...)', () => {
  assert.equal(moved(laneCurves([], 'Nobody')), false);
  assert.equal(autoText([[1, 0], [2.5, -6.5]]), 'auto(1:0, 2.5:-6.5)');
});
