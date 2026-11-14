import test from 'node:test';
import assert from 'node:assert/strict';
import { GOOD_STATUSES, takeFromReport } from '../build-report-take.mjs';

const report = (...blocks) => ({ blocks });

test('a block that was rendered, reused from the cache or adopted gives its take', () => {
  for (const status of GOOD_STATUSES) {
    const r = takeFromReport(report({ block: 'seq', status, takeId: 't-1' }), 'seq');
    assert.deepEqual(r, { ok: true, take: 't-1', message: '' });
  }
});

test('a failed build gives no take, so an earlier take is never offered as the new one (issue 233)', () => {
  const r = takeFromReport(report({ block: 'seq', status: 'failed', message: 'AMP2001: bad note' }), 'seq');
  assert.equal(r.ok, false);
  assert.equal(r.take, null);
  assert.match(r.message, /could not be built \(failed\): AMP2001: bad note/);
});

test('a cancelled, skipped, stale or waiting block gives no take', () => {
  for (const status of ['cancelled', 'skipped', 'stale', 'waiting', 'disabled']) {
    const r = takeFromReport(report({ block: 'seq', status, takeId: 'old-take' }), 'seq');
    assert.equal(r.ok, false, status);
    assert.equal(r.take, null, status);
  }
});

test('a good status without a take id gives no take', () => {
  assert.equal(takeFromReport(report({ block: 'seq', status: 'rendered' }), 'seq').ok, false);
  assert.equal(takeFromReport(report({ block: 'seq', status: 'rendered', takeId: '' }), 'seq').ok, false);
});

test('no report at all (the build failed or was refused) gives no take', () => {
  assert.equal(takeFromReport(null, 'seq').ok, false);
  assert.equal(takeFromReport(undefined, 'seq').ok, false);
  assert.match(takeFromReport(null, 'seq').message, /could not be built/);
});

test('a report without the block, or with another block only, gives no take', () => {
  assert.equal(takeFromReport({}, 'seq').ok, false);
  assert.equal(takeFromReport(report({ block: 'other', status: 'rendered', takeId: 't' }), 'seq').ok, false);
});

test('the block is found among many, and the answer of the server may use capital letters', () => {
  const r = takeFromReport(report(
    { block: 'a', status: 'failed' },
    { Block: 'seq', Status: 'cached', TakeId: 't-9' },
    { block: 'b', status: 'rendered', takeId: 't-2' },
  ), 'seq');
  assert.deepEqual(r, { ok: true, take: 't-9', message: '' });
});

test('a status without a message still says what to do', () => {
  assert.match(takeFromReport(report({ block: 'seq', status: 'cancelled' }), 'seq').message, /See the build log/);
  assert.match(takeFromReport(report({ block: 'seq' }), 'seq').message, /no result/);
});
