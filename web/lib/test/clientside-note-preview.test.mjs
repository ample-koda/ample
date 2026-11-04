import test from 'node:test';
import assert from 'node:assert/strict';
import { selectPreviewVoice, previewBadge, createNoteCache, warmOrder, velocityBucket, auditionSeconds, previewLevel, createVoicePool, createLatencyProbe, latencyBudget, PLAYBACK_LEVEL } from '../clientside-note-preview.mjs';

const route = (tpl, params = {}) => ({ node: { tpl, params }, chain: [] });

test('renderer selection per instrument', () => {
  assert.equal(selectPreviewVoice(route('synth')).kind, 'synth');
  assert.equal(selectPreviewVoice(route('piano')).kind, 'piano');
  assert.equal(selectPreviewVoice(route('piano', { set: 'felt-piano' })).kind, 'piano');
  assert.equal(selectPreviewVoice(route('piano', { set: 'grand' })).kind, 'pitched-set');
  assert.equal(selectPreviewVoice(route('bass')).kind, 'bass');
  assert.equal(selectPreviewVoice(route('bass', { set: 'upright' })).kind, 'pitched-set');
  assert.equal(selectPreviewVoice(route('guitar')).kind, 'guitar');
  assert.equal(selectPreviewVoice(route('sampler')).kind, 'sampler');
  const gm = selectPreviewVoice(route('gm'));
  assert.equal(gm.kind, 'gm');
  assert.equal(gm.local, false);
});

test('fallback for server-only, humanized, expressive and unrouted cases', () => {
  assert.equal(selectPreviewVoice(null).kind, 'fallback');
  assert.equal(selectPreviewVoice(route('drums')).kind, 'fallback');
  assert.equal(selectPreviewVoice(route('piano', { humanize: '0.3' })).reason, 'humanize');
  assert.equal(selectPreviewVoice(route('synth'), { slide: 1 }).reason, 'expression');
  assert.equal(selectPreviewVoice(route('synth'), { pan: 20 }).kind, 'synth');
  assert.equal(selectPreviewVoice(route('piano'), { pan: 20 }).kind, 'fallback');
});

test('the badge is approximate only for the sketch voice or while warming', () => {
  assert.equal(previewBadge({ kind: 'fallback' }, false).approximate, true);
  assert.equal(previewBadge({ kind: 'synth' }, false).approximate, true);
  assert.equal(previewBadge({ kind: 'synth' }, true).approximate, false);
});

test('note cache is LRU and capped by bytes', () => {
  const cache = createNoteCache(100, (v) => v.size);
  cache.set('a', { size: 40 });
  cache.set('b', { size: 40 });
  cache.get('a');
  cache.set('c', { size: 40 });
  assert.equal(cache.has('b'), false);
  assert.equal(cache.has('a'), true);
  assert.equal(cache.has('c'), true);
  assert.equal(cache.bytes, 80);
  assert.equal(cache.set('big', { size: 101 }), false);
  cache.set('a', { size: 10 });
  assert.equal(cache.bytes, 50);
  cache.clear();
  assert.equal(cache.size, 0);
  assert.equal(cache.bytes, 0);
});

test('warm order: used pitches first, then the viewport window around its middle', () => {
  const order = warmOrder([64, 60], 72, 60, 10);
  assert.deepEqual(order.slice(0, 2), [60, 64]);
  assert.equal(new Set(order).size, order.length);
  assert.equal(order.length, 10);
  assert.ok(warmOrder([], 130, 120).every((p) => p >= 0 && p <= 127));
});

test('velocity bucket, audition length and level', () => {
  assert.equal(velocityBucket(100), velocityBucket(98));
  assert.ok(velocityBucket(0) >= 1 && velocityBucket(500) <= 127);
  assert.equal(auditionSeconds(0), 0.12);
  assert.equal(auditionSeconds(99999), 4);
  assert.equal(auditionSeconds(1500), 1.5);
  assert.equal(previewLevel(2), PLAYBACK_LEVEL);
  assert.equal(previewLevel(0.2), 0.2);
  assert.equal(previewLevel(NaN), PLAYBACK_LEVEL);
});

const handle = (log, name) => ({ stop: () => log.push(name) });

test('nine rapid triggers never exceed 8 voices and cut the oldest', () => {
  const log = [], pool = createVoicePool(8);
  for (let i = 0; i < 9; i++) { pool.start(handle(log, i)); assert.ok(pool.size <= 8); }
  assert.deepEqual(log, [0]);
  pool.cutAll();
  assert.equal(pool.size, 0);
});

test('mono starts cut earlier mono voices only', () => {
  const log = [], pool = createVoicePool(8);
  pool.start(handle(log, 'chord'));
  pool.start(handle(log, 'm1'), { mono: true });
  const id = pool.start(handle(log, 'm2'), { mono: true });
  assert.deepEqual(log, ['m1']);
  assert.equal(pool.release(id), true);
  assert.equal(pool.release(id), false);
  assert.deepEqual(log, ['m1', 'm2']);
});

test('latency probe reports p95 against the budget', () => {
  const probe = createLatencyProbe();
  for (let i = 0; i < 20; i++) probe.record(10 + i, true);
  assert.equal(probe.summary(true).withinBudget, true);
  probe.record(500, true);
  probe.record(500, true);
  assert.equal(probe.summary(true).withinBudget, false);
  assert.equal(probe.summary(false).count, 0);
  assert.equal(latencyBudget(true), 40);
  assert.equal(latencyBudget(false), 15);
});
