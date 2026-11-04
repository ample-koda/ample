import test from 'node:test';
import assert from 'node:assert/strict';
import { acceptRecoveryOnOpen, consumeRecoveryAcceptance, pruneRecoveryAcceptances } from '../work-state.mjs';
import { listRecoveryDrafts, saveRecoveryDraft } from '../recovery-drafts.mjs';
import { wordRangeBefore } from '../word-range.mjs';

const mem = () => { const m = new Map(); return { get length() { return m.size; }, key: (i) => [...m.keys()][i] ?? null, getItem: (k) => (m.has(k) ? m.get(k) : null), setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k) }; };

test('acceptance is bound to the saved text', () => {
  const s = mem();
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'source', 5, 'saved v1');
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 5, 'saved v2'), false);
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'source', 5, 'saved v1');
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 5, 'saved v1'), true);
  acceptRecoveryOnOpen(s, 'p', 'a.amplet', 'source', 5, 'saved v1');
  assert.equal(consumeRecoveryAcceptance(s, 'p', 'a.amplet', 'source', 6, 'saved v1'), false);
});
test('old formats are still read', () => {
  const s = mem();
  s.setItem('ample.recovery.accept:p:work:x', 'u7'); assert.equal(consumeRecoveryAcceptance(s, 'p', 'x', 'work', 7, 'any'), true);
  s.setItem('ample.recovery.accept:p:work:x', '1'); assert.equal(consumeRecoveryAcceptance(s, 'p', 'x', 'work', 9, 'any'), true);
});
test('old acceptances and those without a draft are pruned', () => {
  const s = mem();
  acceptRecoveryOnOpen(s, 'p', 'old', 'work', 1, 't', 1000);
  acceptRecoveryOnOpen(s, 'p', 'gone', 'work', 1, 't', 5 * 86400000);
  acceptRecoveryOnOpen(s, 'p', 'live', 'work', 1, 't', 20 * 86400000);
  assert.equal(pruneRecoveryAcceptances(s, 'p', new Set(['work:live', 'work:old']), 40 * 86400000), 2);
  assert.equal(s.length, 1);
});
test('one corrupt draft does not hide the others', () => {
  const s = mem();
  saveRecoveryDraft(s, 'p', 'a', 'A', 'a0', 1);
  s.setItem('ample.source-draft:p:bad', '{not json');
  saveRecoveryDraft(s, 'p', 'c', 'C', 'c0', 3);
  assert.deepEqual(listRecoveryDrafts(s, 'p').map((d) => d.path), ['c', 'a']);
});
test('word range for settings values', () => {
  assert.equal(wordRangeBefore('settings { acestep.back'), 'acestep.back');
  assert.equal(wordRangeBefore('settings { model: C:\Models\ace'), 'C:\Models\ace');
  assert.equal(wordRangeBefore('settings { model: "C:\Mod'), 'C:\Mod');
  assert.equal(wordRangeBefore('settings { dir: a/b/c'), 'a/b/c');
  assert.equal(wordRangeBefore('track @dr'), '@dr');
});
