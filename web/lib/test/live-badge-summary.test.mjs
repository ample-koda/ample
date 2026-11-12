import assert from 'node:assert/strict';
import test from 'node:test';
import { badgeSummary } from '../live-badge-summary.mjs';

test('the summary groups the parameters of a panel by what the live preview does with them', () => {
  const mixer = badgeSummary('mixer', ['gains', 'pan', 'channels']);
  assert.deepEqual(mixer.map((g) => [g.text, g.names]), [['live', ['gains', 'pan']], ['needs build', ['channels']]]);
  assert.deepEqual(badgeSummary('volume', ['left_gain', 'right_gain', 'linked']).map((g) => g.kind), ['param', 'offline']);
  assert.deepEqual(badgeSummary('drums', ['pattern']).map((g) => g.text), ['re-renders']);
  assert.deepEqual(badgeSummary('volume', []), []);
});
