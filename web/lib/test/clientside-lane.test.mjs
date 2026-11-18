import test from 'node:test';
import assert from 'node:assert/strict';
import { laneRole } from '../clientside-lane.mjs';

test('every lane output is a role to ask the server for: lane1, lane2 and more', () => {
  for (const lane of ['lane1', 'lane2', 'lane3', 'lane10', 'lane12']) assert.equal(laneRole(lane), lane);
});

test('every other output is the mix of all lanes: no role', () => {
  for (const output of ['audio', 'mix', '', undefined, null, 'lane', 'laned', 'lane1a', 'xlane1', 'lane-1', 'Lane1']) assert.equal(laneRole(output), '');
});
