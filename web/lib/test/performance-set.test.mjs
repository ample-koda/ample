import test from 'node:test';
import assert from 'node:assert/strict';
import { classifyClip, prepareSet, invalidated, invalidate, isAvailable, READY_LIVE, READY_PREPARED, UNAVAILABLE } from '../performance-set.mjs';

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

test('a clip larger than the whole budget is unavailable, one that fits exactly is admitted', () => {
  const big = [{ id: 1, plan: live, assets: new Set(['chain:a']) }];
  const r = prepareSet(big, { sizes: new Map([['chain:a', 101]]), budget: 100 });
  assert.equal(r.clips[0].state, UNAVAILABLE); assert.equal(r.bytes, 0); assert.equal(r.pinned.size, 0);
  const fit = prepareSet(big, { sizes: new Map([['chain:a', 100]]), budget: 100 });
  assert.equal(fit.clips[0].state, READY_LIVE); assert.equal(fit.bytes, 100); assert.equal(fit.clips[0].bytes, 100);
});
test('the same asset twice in a clip, or in two clips, is counted once; admission order is priority', () => {
  const twice = { ok: true, items: [src('a'), src('a')] };
  const clips = [{ id: 1, plan: twice, assets: new Set(['chain:a']) }, { id: 2, plan: live, assets: new Set(['chain:a']) },
    { id: 3, plan: { ok: true, items: [src('c')] }, assets: new Set(['chain:c']) }];
  const sizes = new Map([['chain:a', 60], ['chain:c', 50]]);
  const r = prepareSet(clips, { sizes, budget: 100 });
  assert.equal(r.clips[0].state, READY_LIVE); assert.equal(r.clips[1].state, READY_LIVE);
  assert.equal(r.clips[2].state, UNAVAILABLE); assert.equal(r.bytes, 60);
  assert.match(r.clips[2].reason, /needs 50 more bytes, 40 left/);
  const reversed = prepareSet([clips[2], clips[0], clips[1]], { sizes, budget: 100 });
  assert.equal(reversed.clips[0].state, READY_LIVE); assert.equal(reversed.clips[1].state, UNAVAILABLE);
});
test('an asset of unknown size cannot be admitted under a finite budget (but is under no budget)', () => {
  const clips = [{ id: 1, plan: live, assets: new Set(['chain:a']) }];
  const r = prepareSet(clips, { budget: 1000 });
  assert.equal(r.clips[0].state, UNAVAILABLE); assert.match(r.clips[0].reason, /unknown/);
  assert.equal(prepareSet(clips).clips[0].state, READY_LIVE);
});
test('a bad budget or a duplicate id is refused instead of silently admitting everything', () => {
  const clips = [{ id: 1, plan: live, assets: new Set(['chain:a']) }];
  assert.throws(() => prepareSet(clips, { budget: NaN }), RangeError);
  assert.throws(() => prepareSet(clips, { budget: -1 }), RangeError);
  assert.throws(() => prepareSet([...clips, ...clips]), /duplicate/);
  assert.equal(prepareSet(clips, { sizes: new Map([['chain:a', 5]]), budget: 0 }).clips[0].state, UNAVAILABLE);
});
test('a plan without items and assets given as an array do not crash', () => {
  assert.equal(classifyClip({ id: 1, plan: { ok: true } }).state, READY_LIVE);
  assert.equal(classifyClip({ id: 1, plan: live, assets: ['chain:a'] }).state, READY_LIVE);
});
test('invalidate returns a new report: the clip is unavailable, its pins are released, shared pins stay', () => {
  const clips = [{ id: 1, plan: live, assets: new Set(['chain:a']) }, { id: 2, plan: live, assets: new Set(['chain:a']) },
    { id: 3, plan: { ok: true, items: [src('c')] }, assets: new Set(['chain:c']) }];
  const r = prepareSet(clips, { sizes: new Map([['chain:a', 60], ['chain:c', 50]]), budget: 200 });
  const r2 = invalidate(r, ['chain:c']);
  assert.equal(r2.clips[2].state, UNAVAILABLE); assert.match(r2.clips[2].reason, /changed/);
  assert.equal(r2.bytes, 60); assert.equal(r2.ok, false); assert.equal(r2.pinned.has('chain:c'), false);
  assert.equal(r.clips[2].state, READY_LIVE); assert.equal(r.bytes, 110); // the old report is untouched
  const shared = invalidate(r, ['chain:a']);
  assert.deepEqual(shared.clips.map((c) => c.state), [UNAVAILABLE, UNAVAILABLE, READY_LIVE]);
  assert.equal(shared.bytes, 50);
  assert.equal(invalidate(r, ['nothing']), r);
});
test('reports cannot be changed by the caller, and the pinned set is a copy', () => {
  const r = prepareSet([{ id: 1, plan: live, assets: new Set(['chain:a']) }], { sizes: new Map([['chain:a', 10]]) });
  assert.throws(() => { 'use strict'; r.clips[0].state = READY_PREPARED; }, TypeError);
  assert.throws(() => { 'use strict'; r.clips[0].pins.push('x'); }, TypeError);
  assert.throws(() => { 'use strict'; r.clips.push({}); }, TypeError);
  r.pinned.add('x'); assert.equal(invalidated(r, ['x']).length, 0);
});
test('isAvailable: ready live and prepared yes, unavailable and unknown ids no', () => {
  const clips = [{ id: 1, plan: live, assets: new Set(['chain:a']) },
    { id: 2, plan: conv, assets: new Set(['chain:b']), prepared: { key: 'take:2', bytes: 10 } }, { id: 3, plan: conv, assets: new Set(['chain:b']) }];
  const r = prepareSet(clips);
  assert.deepEqual([1, 2, 3, 4].map((id) => isAvailable(r, id)), [true, true, false, false]);
  assert.equal(isAvailable(null, 1), false);
});
