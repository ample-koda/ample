import test from 'node:test';
import assert from 'node:assert/strict';
import { endsTracking, pillAction } from '../task-pill.mjs';

test('a failed status request keeps the task and says it is reconnecting (issue 223)', () => {
  assert.equal(pillAction({ error: new Error('network') }), 'reconnecting');
  assert.equal(pillAction({ error: { status: 500 } }), 'reconnecting');
  assert.equal(pillAction({ error: { status: 0 } }), 'reconnecting');
  assert.equal(pillAction({ error: { status: 503 } }), 'reconnecting');
});

test('only an answer that says the task does not exist ends the tracking', () => {
  assert.equal(pillAction({ error: { status: 404 } }), 'gone');
  assert.equal(pillAction({ error: { status: 410 } }), 'gone');
  assert.equal(pillAction({ task: null }), 'gone');
  assert.equal(pillAction(), 'gone');
  assert.equal(endsTracking('gone', null), true);
});

test('a task that is over loses its pill; applied and rejected also end the tracking, cancelled does not', () => {
  assert.equal(pillAction({ task: { status: 'rejected' } }), 'over');
  assert.equal(pillAction({ task: { status: 'cancelled' } }), 'over');
  assert.equal(pillAction({ task: { status: 'ready', applied: true } }), 'over');
  assert.equal(endsTracking('over', { status: 'rejected' }), true);
  assert.equal(endsTracking('over', { status: 'ready', applied: true }), true);
  assert.equal(endsTracking('over', { status: 'cancelled' }), false);
});

test('a task that works or waits is shown, and showing or reconnecting never ends the tracking', () => {
  for (const status of ['queued', 'preparing', 'running', 'validating', 'waiting-approval', 'ready', 'failed']) {
    assert.equal(pillAction({ task: { status } }), 'show');
  }
  assert.equal(endsTracking('show', { status: 'running' }), false);
  assert.equal(endsTracking('reconnecting', null), false);
});
