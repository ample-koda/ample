import test from 'node:test';
import assert from 'node:assert/strict';
import { effortStrength, modelStrength, powerOf, TIERS } from '../koda-power.mjs';

const m = (id, name = '', description = '', defaultEffort = 'medium') => ({ id, name, description, defaultEffort });

test('a small quick model looks weaker than a large one, and more thinking makes the same model stronger', () => {
  assert.ok(modelStrength(m('gpt-5.1-codex-mini')) < modelStrength(m('gpt-5.1-codex')));
  assert.ok(modelStrength(m('gpt-5.1-codex')) < modelStrength(m('gpt-5.1-codex-max')));
  const model = m('gpt-5-codex');
  const scores = ['low', 'medium', 'high', 'xhigh'].map((e) => powerOf(model, e).score);
  assert.deepEqual([...scores].sort((a, b) => a - b), scores);
  assert.ok(scores[3] > scores[0] + 0.2);
});

test('the scale runs from relaxed to brilliant and the extremes are reached by the extreme settings', () => {
  assert.equal(powerOf(m('gpt-5-nano'), 'minimal').tier, 0);
  assert.equal(powerOf(m('gpt-6-pro', '', 'the most capable model'), 'xhigh').tier, 4);
  assert.equal(TIERS.length, 5);
});

test('the level a model decides for itself counts, an unknown model and an empty choice are the middle', () => {
  assert.equal(effortStrength('', m('x', '', '', 'high')), effortStrength('high'));
  assert.equal(effortStrength('default', m('x', '', '', 'low')), effortStrength('low'));
  const middle = powerOf(null, '');
  assert.ok(middle.tier >= 1 && middle.tier <= 3);
});
