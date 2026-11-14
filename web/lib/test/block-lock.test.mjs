import test from 'node:test';
import assert from 'node:assert/strict';
import { isBlockLocked } from '../block-lock.mjs';

const nodes = [{ id: 'a', flags: ['lock'] }, { id: 'b', flags: [] }, { id: 'c' }, { id: 'd', flags: ['mute', 'lock'] }];

test('a block with the lock flag is locked (issue 115)', () => {
  assert.equal(isBlockLocked(nodes, 'a'), true);
  assert.equal(isBlockLocked(nodes, 'd'), true);
});

test('a block without it, without flags, or that is gone is not locked', () => {
  assert.equal(isBlockLocked(nodes, 'b'), false);
  assert.equal(isBlockLocked(nodes, 'c'), false);
  assert.equal(isBlockLocked(nodes, 'zzz'), false);
  assert.equal(isBlockLocked(undefined, 'a'), false);
});

test('the answer follows the nodes it is given, so a lock set later is seen by a window opened earlier', () => {
  const before = [{ id: 'a', flags: [] }];
  const after = [{ id: 'a', flags: ['lock'] }];
  assert.equal(isBlockLocked(before, 'a'), false);
  assert.equal(isBlockLocked(after, 'a'), true);
});
