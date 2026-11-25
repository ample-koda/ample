import test from 'node:test';
import assert from 'node:assert/strict';
import { createPendingSteps } from '../pending-steps.mjs';

test('three quick keys before the first answer step on from each other, a pause or another control counts the drawn value again (bug 10)', () => {
  let clock = 0;
  const steps = createPendingSteps({ now: () => clock, keepMs: 700 });
  let at = steps.from('clip', 5);
  for (let key = 0; key < 3; key++) { at = steps.from('clip', 5) + 1; steps.asked('clip', at); clock += 30; }
  assert.equal(at, 8);
  assert.equal(steps.from('other', 2), 2);
  clock += 1000;
  assert.equal(steps.from('clip', 6), 6);
  steps.asked('clip', 7); steps.forget();
  assert.equal(steps.from('clip', 6), 6);
});
