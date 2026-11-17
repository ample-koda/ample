import test from 'node:test';
import assert from 'node:assert/strict';
import { MODES, clipPlaysIn, liveOnlyClips, modeOf } from '../performance-mode.mjs';

test('a mode is read from anything and only live is live', () => {
  assert.deepEqual(MODES, ['takes', 'live']);
  assert.equal(modeOf('live'), 'live');
  for (const v of ['takes', '', undefined, null, 'LIVE', 3]) assert.equal(modeOf(v), 'takes');
});

test('a live chain plays in the live mode only; a take and a pattern play in both', () => {
  assert.equal(clipPlaysIn('chain:hall', 'takes'), false);
  assert.equal(clipPlaysIn('chain:hall', 'live'), true);
  assert.equal(clipPlaysIn('kick', 'takes'), true);
  assert.equal(clipPlaysIn('pattern:seq:A', 'takes'), true);
  assert.equal(clipPlaysIn('chain:hall', 'nonsense'), false);
});

test('the clips that do not play in a mode are listed once each', () => {
  const layout = { lanes: [{ name: 'A', clips: [{ id: 'kick', label: 'Kick' }, { id: 'chain:hall', label: 'Hall (live chain)' }] }, { name: 'B', clips: [{ id: 'chain:hall', label: 'Hall (live chain)' }, { id: 'chain:eq', label: 'Eq (live chain)' }] }] };
  assert.deepEqual(liveOnlyClips(layout, 'takes').map((c) => c.id), ['chain:hall', 'chain:eq']);
  assert.deepEqual(liveOnlyClips(layout, 'live'), []);
  assert.deepEqual(liveOnlyClips(undefined, 'takes'), []);
});
