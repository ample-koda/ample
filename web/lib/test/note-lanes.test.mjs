import test from 'node:test';
import assert from 'node:assert/strict';
import { LANE_DEFS, laneValueOf, laneClamp, laneFromY, laneFraction, laneRamp } from '../note-lanes.mjs';

test('a note without a value shows the lane default, and a release lane starts at 0', () => {
  assert.equal(laneValueOf({}, LANE_DEFS.cutoff), 100);
  assert.equal(laneValueOf({ cut: 40 }, LANE_DEFS.cutoff), 40);
  assert.equal(laneValueOf({}, LANE_DEFS.release), 0);
  assert.equal(laneValueOf({ pan: -30 }, LANE_DEFS.pan), -30);
});

test('values are whole numbers inside the range of the lane', () => {
  assert.equal(laneClamp(130.4, LANE_DEFS.velocity), 127);
  assert.equal(laneClamp(-250, LANE_DEFS.pan), -100);
  assert.equal(laneClamp(12.6, LANE_DEFS.fine), 13);
});

test('the top of the lane is the maximum and the bottom the minimum; the middle of a bipolar lane is 0', () => {
  assert.equal(laneFromY(0, 58, LANE_DEFS.pan), 100);
  assert.equal(laneFromY(58, 58, LANE_DEFS.pan), -100);
  assert.equal(laneFromY(29, 58, LANE_DEFS.pan), 0);
  assert.equal(laneFromY(0, 58, LANE_DEFS.velocity), 127);
  assert.equal(laneFromY(58, 58, LANE_DEFS.velocity), 1);
  assert.equal(laneFromY(-10, 58, LANE_DEFS.cutoff), 100);
});

test('a stem fills the share of the range its value covers', () => {
  assert.equal(laneFraction(0, LANE_DEFS.pan), 0.5);
  assert.equal(laneFraction(100, LANE_DEFS.cutoff), 1);
  assert.equal(laneFraction(2500, LANE_DEFS.release), 0.5);
});

test('a ramp sets the notes between its two ends, whichever way it is dragged', () => {
  const rows = [{ index: 0, at: 0 }, { index: 1, at: 500 }, { index: 2, at: 1000 }, { index: 3, at: 2000 }];
  assert.deepEqual([...laneRamp(rows, 0, -100, 1000, 100, LANE_DEFS.pan)], [[0, -100], [1, 0], [2, 100]]);
  assert.deepEqual([...laneRamp(rows, 1000, 100, 0, -100, LANE_DEFS.pan)], [[0, -100], [1, 0], [2, 100]]);
});
