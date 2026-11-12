import test from 'node:test';
import assert from 'node:assert/strict';
import { createTakeCache } from '../take-cache.mjs';

const buf = (frames) => ({ length: frames, numberOfChannels: 2 });   // frames * 2 * 4 bytes
const MB = 1024 * 1024;

test('a second request shares the first load', async () => {
  const cache = createTakeCache(); let calls = 0;
  const load = async () => { calls++; return buf(1000); };
  const [a, b] = await Promise.all([cache.getDecoded('t1', load), cache.getDecoded('t1', load)]);
  assert.equal(a, b); assert.equal(calls, 1); assert.equal(cache.bytes(), 8000);
});

test('the least recently used unpinned entry goes first', async () => {
  const cache = createTakeCache({ budgetMb: 2 });          // 2 MiB = 262144 stereo frames
  const frames = 100000;                                    // 0.8 MB each
  await cache.getDecoded('a', async () => buf(frames));
  await cache.getDecoded('b', async () => buf(frames));
  await cache.getDecoded('a', async () => assert.fail('cached'));   // touch a: b is now the oldest
  await cache.getDecoded('c', async () => buf(frames));            // 2.4 MB > 2 MiB: evict b
  assert.ok(cache.has('a')); assert.ok(!cache.has('b')); assert.ok(cache.has('c'));
  assert.ok(cache.bytes() <= 2 * MB);
});

test('a pinned entry is never evicted, and goes when unpinned', async () => {
  const cache = createTakeCache({ budgetMb: 1 });
  await cache.getDecoded('a', async () => buf(100000)); cache.pin('a');
  await cache.getDecoded('b', async () => buf(100000));   // over budget: b is evicted, a stays
  assert.ok(cache.has('a')); assert.ok(!cache.has('b'));
  await cache.getDecoded('c', async () => buf(100000));
  assert.ok(cache.has('a'));
  cache.unpin('a'); assert.ok(!cache.has('a') || cache.bytes() <= 1 * MB);
});

test('a failed load is not cached; a range is its own entry; setBudget evicts', async () => {
  const cache = createTakeCache();
  await assert.rejects(cache.getDecoded('x', async () => { throw new Error('no'); }));
  assert.ok(!cache.has('x'));
  await cache.getDecoded('t', async () => buf(200000));
  await cache.getDecoded('t', async () => buf(50000), { range: { start: 0, end: 4 } });
  assert.equal(cache.size(), 2);
  cache.setBudget(0.5);
  assert.ok(cache.bytes() <= 0.5 * MB);
});
