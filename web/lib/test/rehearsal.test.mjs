import test from 'node:test';
import assert from 'node:assert/strict';
import { createRehearsal, optionalBypassList, fallbackIndicator, chooseFallbacks } from '../rehearsal.mjs';

const items = [{ id: 'a', tpl: 'reverb' }, { id: 'b', tpl: 'limiter' }, { id: 'c', tpl: 'delay' }];

test('output-critical stages are not offered, heaviest first', () => {
  assert.deepEqual(optionalBypassList(items).map((i) => i.id), ['a', 'c']);
  assert.deepEqual(chooseFallbacks(items, ['b', 'c']), ['c']);
});

test('measured and estimated headroom are reported separately', () => {
  const est = createRehearsal({ items, measured: false });
  est.load('steady', 0.5); est.miss('steady', 2);
  const r = est.report();
  assert.equal(r.measuredHeadroom, null);
  assert.equal(r.missedDeadlines, 2);
  assert.equal(r.verdict, 'at-risk');
  const m = createRehearsal({ items, measured: true });
  m.load('scene:A', 0.3); m.load('steady', 0.4);
  const rm = m.report();
  assert.ok(Math.abs(rm.measuredHeadroom - 0.6) < 1e-9);
  assert.equal(rm.verdict, 'ok');
});

test('fallback indicator names bypassed blocks', () => {
  assert.equal(fallbackIndicator([{ id: 'a', cpuBypassed: true }, { id: 'c' }], (id) => id.toUpperCase()), 'Fallback active: A bypassed');
  assert.equal(fallbackIndicator([{ id: 'c' }]), '');
});
