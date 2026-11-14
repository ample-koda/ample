import test from 'node:test';
import assert from 'node:assert/strict';
import { announcement, buildState, percentOf } from '../build-status.mjs';

test('a build or a preview that runs is working, otherwise failed blocks decide (issue 124)', () => {
  assert.equal(buildState({ running: true }), 'working');
  assert.equal(buildState({ previewRunning: true, failed: 3 }), 'working');
  assert.equal(buildState({ failed: 2 }), 'failed');
  assert.equal(buildState({}), 'ready');
  assert.equal(buildState(), 'ready');
});

test('the percentage is whole, between 0 and 100, and 0 without a total', () => {
  assert.equal(percentOf(1, 3), 33);
  assert.equal(percentOf(3, 3), 100);
  assert.equal(percentOf(5, 3), 100);
  assert.equal(percentOf(0, 0), 0);
  assert.equal(percentOf(-1, 4), 0);
});

test('a start, a finish and a failure are said once; ticks and the first state are not', () => {
  assert.equal(announcement(null, 'ready'), '');
  assert.equal(announcement('ready', 'working'), 'Building');
  assert.equal(announcement('working', 'working'), '');
  assert.equal(announcement('working', 'ready'), 'Build finished');
  assert.equal(announcement('working', 'failed', 1), 'Build finished: 1 block failed');
  assert.equal(announcement('working', 'failed', 4), 'Build finished: 4 blocks failed');
  assert.equal(announcement('failed', 'failed', 4), '');
});

test('going from idle to idle or failed to ready says nothing', () => {
  assert.equal(announcement('ready', 'ready'), '');
  assert.equal(announcement('failed', 'ready'), '');
});
