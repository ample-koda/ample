import test from 'node:test';
import assert from 'node:assert/strict';
import { FORMAT_STALE_MESSAGE, formatStillApplies, planSaveAll } from '../source-guards.mjs';

test('a formatting answer applies only to the text that was sent', () => {
  assert.equal(formatStillApplies('a: 1\n', 'a: 1\n'), true);
  assert.equal(formatStillApplies('a: 1\n', 'a: 1\nb: 2\n'), false);   // typed while the answer was pending
  assert.equal(formatStillApplies('a: 1\n', ''), false);
  assert.equal(formatStillApplies('', ''), true);
});

test('a missing or non-text sent value never applies', () => {
  assert.equal(formatStillApplies(undefined, undefined), false);
  assert.equal(formatStillApplies(null, null), false);
  assert.equal(formatStillApplies(5, 5), false);
});

test('the message tells the person what to do', () => {
  assert.match(FORMAT_STALE_MESSAGE, /Press Format again/);
});

test('Save all with nothing typed and nothing changed says nothing to save', () => {
  assert.deepEqual(planSaveAll({ typed: null, dirty: [] }), { action: 'nothing', message: 'Nothing to save.' });
  assert.equal(planSaveAll().action, 'nothing');
});

test('Save all with typed text in a file that is not otherwise changed reports that it saved it', () => {
  const plan = planSaveAll({ typed: 'sequences/beat.ampleseq', dirty: [] });
  assert.equal(plan.action, 'saved-typed-only');
  assert.equal(plan.message, 'Saved beat.ampleseq.');
});

test('Save all with changed files goes on to ask, also when typed text was applied', () => {
  assert.deepEqual(planSaveAll({ typed: null, dirty: ['song.amplet'] }), { action: 'ask' });
  assert.deepEqual(planSaveAll({ typed: 'song.amplet', dirty: ['song.amplet'] }), { action: 'ask' });
});

test('Save all stops when the typed text could not be applied, even if other files are changed', () => {
  const plan = planSaveAll({ typed: false, dirty: ['song.amplet'] });
  assert.equal(plan.action, 'stop');
  assert.match(plan.message, /could not be applied/);
});
