import test from 'node:test';
import assert from 'node:assert/strict';
import { acceptRecoveryOnOpen, consumeRecoveryAcceptance } from '../work-state.mjs';

const memory = () => { const m = new Map(); return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, v), removeItem: (k) => m.delete(k) }; };

test('an agreement made ahead of time holds for the buffer it was made for, once', () => {
  const s = memory();
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'source', 100);
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 100), true);
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 100), false);
});

test('a buffer that changed after the agreement is asked about again', () => {
  const s = memory();
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'source', 100);
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 200), false);
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 100), false);   // the stale agreement is gone
});

test('an agreement without a stamp (the diff dialog of the file itself) still counts', () => {
  const s = memory();
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'work');
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'work', 5), true);
});
