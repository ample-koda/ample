import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyClip, prepareSet, invalidated, READY_LIVE, READY_PREPARED, UNAVAILABLE } from '../performance-set.mjs';

const src = (take) => ({ kind: 'source', take });
const live = { ok: true, items: [src('a'), { kind: 'effect', tpl: 'reverb', id: 'r' }] };
const conv = { ok: true, items: [src('b'), { kind: 'effect', tpl: 'convolution', id: 'c' }] };

test('live clip with everything in memory is ready live', () => {
  assert.equal(classifyClip({ id: 1, plan: live, assets: new Set(['chain:a']) }).state, READY_LIVE);
});
test('missing impulse falls back to the prepared take, or is unavailable with a reason', () => {
  const assets = new Set(['chain:b']);
  assert.equal(classifyClip({ id: 2, plan: conv, assets, prepared: { key: 'take:2', bytes: 10 } }).state, READY_PREPARED);
  const r = classifyClip({ id: 2, plan: conv, assets });
  assert.equal(r.state, UNAVAILABLE);
  assert.match(r.reason, /impulse of c/);
});
test('unplannable chain uses its reason', () => {
  assert.match(classifyClip({ id: 3, plan: { ok: false, reason: 'nope' } }).reason, /nope/);
});
test('memory budget rejects what does not fit, and changes invalidate', () => {
  const clips = [{ id: 1, plan: live, assets: new Set(['chain:a']) }, { id: 2, plan: conv, assets: new Set(['chain:b']), prepared: { key: 'take:2', bytes: 100 } }];
  const sizes = new Map([['chain:a', 60]]);
  const ok = prepareSet(clips, { sizes, budget: 200 });
  assert.ok(ok.ok); assert.equal(ok.bytes, 160);
  const tight = prepareSet(clips, { sizes, budget: 100 });
  assert.equal(tight.clips[1].state, UNAVAILABLE); assert.match(tight.clips[1].reason, /memory/);
  assert.deepEqual(invalidated(ok, ['chain:a']), [1]);
});
