import test from 'node:test';
import assert from 'node:assert/strict';
import { createRehearsal, optionalBypassList, fallbackIndicator, chooseFallbacks } from '../rehearsal.mjs';

const items = [{ id: 'a', tpl: 'reverb' }, { id: 'b', tpl: 'limiter' }, { id: 'c', tpl: 'delay' }];

test('output-critical stages are not offered, heaviest first', () => {
  assert.deepEqual(optionalBypassList(items).map((i) => i.id), ['a', 'c']);
  assert.deepEqual(chooseFallbacks(items, ['b', 'c']), ['c']);
});

test('routing, volume, pan, case variants, typeless and non-effect items are never offered', () => {
  const all = [{ id: 'v', tpl: 'volume' }, { id: 'p', tpl: 'pan' }, { id: 'L', tpl: 'Limiter' }, { id: 'bus1', tpl: 'bus' }, { id: 's', tpl: 'send' },
    { id: 'x' }, { id: 'src', tpl: 'reverb', kind: 'source' }, { id: 'ok', tpl: 'chorus', kind: 'effect' }, null];
  assert.deepEqual(optionalBypassList(all).map((i) => i.id), ['ok']);
  assert.deepEqual(optionalBypassList(null), []);
});

test('chooseFallbacks drops unknown ids, keeps the chosen order, collapses duplicates, tolerates junk', () => {
  assert.deepEqual(chooseFallbacks(items, ['c', 'zzz', 'a', 'c']), ['c', 'a']);
  assert.deepEqual(chooseFallbacks(items, undefined), []);
  assert.deepEqual(chooseFallbacks(items, 'a'), []);
  assert.deepEqual(chooseFallbacks([], ['a']), []);
});

test('measured and estimated headroom are reported separately', () => {
  const est = createRehearsal({ items, measured: false });
  assert.equal(est.load('steady', 0.5), false);          // no render capacity: readings are not stored as measurements
  est.miss('steady', 2);
  const r = est.report();
  assert.equal(r.measuredHeadroom, null); assert.equal(r.basis, 'estimated');
  assert.equal(r.phases[0].peak, null); assert.equal(r.phases[0].mean, null);
  assert.equal(r.missedDeadlines, 2); assert.equal(r.checkedDeadlines, 2);
  assert.equal(r.verdict, 'at-risk');
  const m = createRehearsal({ items, measured: true });
  m.load('scene:A', 0.3); m.load('steady', 0.4);
  const rm = m.report();
  assert.ok(Math.abs(rm.measuredHeadroom - 0.6) < 1e-9); assert.equal(rm.basis, 'measured');
  assert.equal(rm.verdict, 'ok');
});

test('measured with zero samples gives no measured headroom (not 100 percent) and an untested verdict', () => {
  const r = createRehearsal({ items, measured: true }).report();
  assert.equal(r.measuredHeadroom, null); assert.equal(r.basis, 'estimated'); assert.equal(r.verdict, 'untested');
  assert.equal(r.missedDeadlines, 0); assert.deepEqual(r.phases, []);
  assert.ok(r.estimatedHeadroom > 0 && r.estimatedHeadroom <= 1);
});

test('one sample: peak and mean are that sample; a phase that only has misses has no load figure', () => {
  const m = createRehearsal({ items: [], measured: true });
  m.load('scene:A', 0.5); m.miss('scene:B', 1);
  const r = m.report();
  const a = r.phases.find((p) => p.name === 'scene:A'), b = r.phases.find((p) => p.name === 'scene:B');
  assert.equal(a.peak, 0.5); assert.equal(a.mean, 0.5); assert.equal(a.samples, 1);
  assert.equal(b.peak, null); assert.equal(b.mean, null); assert.equal(b.missed, 1);
  assert.ok(Math.abs(r.measuredHeadroom - 0.5) < 1e-9);
});

test('null, NaN, empty and negative readings (renderCapacity unavailable) are ignored, not counted as zero load', () => {
  const m = createRehearsal({ items: [], measured: true });
  for (const v of [null, undefined, NaN, '', 'x', -1, Infinity]) assert.equal(m.load('steady', v), false);
  assert.equal(m.report().phases.length, 0);
  m.load('steady', 0.8); m.load('steady', null);
  const p = m.report().phases[0];
  assert.equal(p.samples, 1); assert.equal(p.mean, 0.8);
});

test('a scene with no deadline data: load only gives ok/tight, never a claim about deadlines', () => {
  const m = createRehearsal({ items: [], measured: true });
  m.load('scene:A', 0.4);
  const r = m.report();
  assert.equal(r.checkedDeadlines, 0); assert.equal(r.missedDeadlines, 0); assert.equal(r.phases[0].checked, 0);
  assert.equal(r.verdict, 'ok');
  m.deadlines('scene:A', 50, 0);
  assert.equal(m.report().checkedDeadlines, 50);
});

test('deadlines: missed is capped by checked, junk counts are ignored', () => {
  const m = createRehearsal({ items: [], measured: false });
  m.deadlines('steady', 10, 3); m.deadlines('steady', 'x', NaN); m.miss('steady', -2); m.miss('steady', 1.9);
  m.deadlines('scene:A', 2, 5);
  const r = m.report();
  assert.equal(r.missedDeadlines, 3 + 1 + 5); assert.equal(r.checkedDeadlines, 10 + 1 + 5);
  assert.equal(r.verdict, 'at-risk');
});

test('verdict follows the measured peak when there is one, the estimate otherwise; the estimate never says at-risk alone', () => {
  const heavy = Array.from({ length: 12 }, (_, i) => ({ id: `r${i}`, tpl: 'reverb' }));   // cost 120 against budget 80
  const est = createRehearsal({ items: heavy, measured: false });
  est.deadlines('steady', 100, 0);
  let r = est.report();
  assert.equal(r.estimatedHeadroom, 0); assert.equal(r.verdict, 'tight');          // clamped, not negative
  const meas = createRehearsal({ items: heavy, measured: true });
  meas.load('steady', 0.3); meas.deadlines('steady', 100, 0);
  r = meas.report(); assert.equal(r.verdict, 'ok'); assert.equal(r.estimatedHeadroom, 0);   // the measured figure rules, estimate stays labelled
  const hot = createRehearsal({ items: [], measured: true }); hot.load('steady', 0.75); assert.equal(hot.report().verdict, 'tight');
  hot.load('steady', 1.2); const rh = hot.report(); assert.equal(rh.verdict, 'at-risk'); assert.equal(rh.measuredHeadroom, 0);   // over 100 percent clamps to 0
});

test('budget zero or negative falls back to the default instead of dividing by zero', () => {
  for (const budget of [0, -5, NaN]) {
    const r = createRehearsal({ items, measured: false, budget }).report();
    assert.ok(Number.isFinite(r.estimatedHeadroom) && r.estimatedHeadroom >= 0 && r.estimatedHeadroom <= 1);
  }
  const empty = createRehearsal({ measured: false }).report();
  assert.equal(empty.estimatedHeadroom, 1); assert.equal(empty.verdict, 'untested'); assert.deepEqual(empty.optionalBypass, []);
});

test('fallback indicator names bypassed blocks', () => {
  assert.equal(fallbackIndicator([{ id: 'a', cpuBypassed: true }, { id: 'c' }], (id) => id.toUpperCase()), 'Fallback active: A bypassed');
  assert.equal(fallbackIndicator([{ id: 'c' }]), '');
});

test('fallback indicator with several active fallbacks, an empty title, and bad input', () => {
  const chain = [{ id: 'a', cpuBypassed: true }, { id: 'b' }, { id: 'c', cpuBypassed: true }, { id: 'd', cpuBypassed: true }];
  assert.equal(fallbackIndicator(chain, (id) => ({ a: 'Reverb', c: '' }[id])), 'Fallback active: Reverb, c, d bypassed');
  assert.equal(fallbackIndicator(null), ''); assert.equal(fallbackIndicator([null, {}]), '');
});
