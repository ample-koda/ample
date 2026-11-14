import test from 'node:test';
import assert from 'node:assert/strict';
import { buildHeadline, rejectLabel, rejectQuestion, rejectedText } from '../ai-review-text.mjs';

test('the headline of a whole-proposal check is the plain one', () => {
  assert.equal(buildHeadline({ status: 'ok' }), 'Build successful');
  assert.equal(buildHeadline({ status: 'failed' }), 'Build failed');
  assert.equal(buildHeadline({ status: 'running' }), 'Checking the build...');
  assert.equal(buildHeadline({ status: 'pending' }), 'Waiting to check the build...');
  assert.equal(buildHeadline({ status: 'none' }), 'Nothing to build');
  assert.equal(buildHeadline({ status: 'skipped' }), 'Not built (the check could not be set up)');
  assert.equal(buildHeadline({ status: 'skipped', skipRequested: true }), 'Build not run (as requested)');
});

test('a check of one block never says "Build successful" for the proposal (issue 218)', () => {
  const ok = buildHeadline({ status: 'ok', only: 'bass' });
  assert.match(ok, /Only the block bass was built/);
  assert.match(ok, /rest of the proposal was not checked/);
  assert.doesNotMatch(ok, /^Build successful/);
  assert.equal(buildHeadline({ status: 'failed', only: 'bass' }), 'The block bass failed to build');
  assert.equal(buildHeadline({ status: 'running', only: 'bass' }), 'Checking the build...');
});

test('an unknown status is shown as it is and a missing build does not throw', () => {
  assert.equal(buildHeadline({ status: 'weird' }), 'weird');
  assert.equal(buildHeadline(undefined), '');
});

test('Reject is Reject remaining once files were accepted (issue 226)', () => {
  assert.equal(rejectLabel([]), 'Reject');
  assert.equal(rejectLabel(undefined), 'Reject');
  assert.equal(rejectLabel(['a.amplec']), 'Reject remaining');
});

test('the question says what stays applied', () => {
  assert.match(rejectQuestion([]), /Nothing in the project changes/);
  assert.match(rejectQuestion(['a.amplec']), /a\.amplec\) stays applied/);
  assert.match(rejectQuestion(['a.amplec', 'b.txt']), /a\.amplec, b\.txt\) stay applied/);
});

test('the closed proposal lists what was applied and kept, a long list is cut', () => {
  assert.equal(rejectedText([]), 'Rejected. Nothing in the project changed.');
  assert.equal(rejectedText(['a.amplec']), 'The rest was rejected. Already applied and kept: a.amplec.');
  const many = ['1', '2', '3', '4', '5', '6', '7'];
  assert.equal(rejectedText(many), 'The rest was rejected. Already applied and kept: 1, 2, 3, 4, 5 and 2 more.');
});
