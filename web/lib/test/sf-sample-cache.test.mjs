import assert from 'node:assert/strict';
import test from 'node:test';
import { cacheFile, cacheable, createSampleCache, memoryStore, sampleKey } from '../sf-sample-cache.mjs';

const bytes = (n) => new ArrayBuffer(n);

test('a project SoundFont is cached under its size and time, so an edited file is a new name', async () => {
  const list = (modified, size = 10) => async (folder) => { assert.equal(folder, 'sounds'); return [{ name: 'own.sf2', sizeBytes: size, modified }, { name: 'other.sf2', sizeBytes: 1, modified: 'x' }]; };
  assert.equal(await cacheFile('pack:x', list('a')), 'pack:x');
  const first = await cacheFile('sounds/own.sf2', list('t1')), second = await cacheFile('sounds/own.sf2', list('t2')), bigger = await cacheFile('sounds/own.sf2', list('t1', 11));
  assert.equal(first, 'sounds/own.sf2@10:t1'); assert.notEqual(first, second); assert.notEqual(first, bigger);
  assert.equal(await cacheFile('sounds/missing.sf2', list('t1')), null);
  assert.equal(await cacheFile('sounds/own.sf2', async () => { throw new Error('offline'); }), null);
});

test('only packs are cached by name, and the key snaps the velocity to four layers and the length to 50 ms', () => {
  assert.equal(cacheable('pack:musescore-general'), true); assert.equal(cacheable('sounds/my.sf2'), true); assert.equal(cacheable(''), false);
  const a = sampleKey({ file: 'pack:x', bank: 0, program: 5, channel: 0, pitch: 60, velocity: 90, ticks: 500_000 });
  assert.equal(a, sampleKey({ file: 'pack:x', bank: 0, program: 5, channel: 0, pitch: 60, velocity: 100, ticks: 510_000 }));   // same layer, same 50 ms step
  assert.notEqual(a, sampleKey({ file: 'pack:x', bank: 0, program: 5, channel: 0, pitch: 61, velocity: 90, ticks: 500_000 }));
  assert.notEqual(a, sampleKey({ file: 'pack:x', bank: 0, program: 5, channel: 0, pitch: 60, velocity: 20, ticks: 500_000 }));
  assert.notEqual(a, sampleKey({ file: 'pack:y', bank: 0, program: 5, channel: 0, pitch: 60, velocity: 90, ticks: 500_000 }));
  assert.match(sampleKey({ file: 'pack:x', drum: true, pitch: 36 }), /\|kit\|/);
});

test('a stored sample comes back, a missing one is null, and a store that is not there means no cache', async () => {
  const cache = createSampleCache(memoryStore());
  assert.equal(await cache.get('a'), null);
  const data = bytes(1000);
  assert.equal(await cache.put('a', data), true);
  assert.equal(await cache.get('a'), data);
  const none = createSampleCache(null);
  assert.equal(await none.get('a'), null); assert.equal(await none.put('a', data), false);
});

test('the least recently used samples go first when the budget is full, and a hit makes a sample recent', async () => {
  let clock = 0;
  const cache = createSampleCache(memoryStore(), { maxBytes: 1000, now: () => ++clock });
  await cache.put('a', bytes(200)); await cache.put('b', bytes(200)); await cache.put('c', bytes(200)); await cache.put('d', bytes(200));
  await cache.get('a');   // a is recent now, b is the oldest
  await cache.put('e', bytes(200)); await cache.put('f', bytes(200));   // 1200 bytes: over the budget, down to 800 or less
  assert.equal(await cache.get('b'), null); assert.notEqual(await cache.get('a'), null); assert.notEqual(await cache.get('f'), null);
  assert.ok(await cache.bytes() <= 1000);
});

test('one sample larger than a quarter of the budget is not kept', async () => {
  const cache = createSampleCache(memoryStore(), { maxBytes: 1000 });
  assert.equal(await cache.put('big', bytes(300)), false);
  assert.equal(await cache.put('ok', bytes(250)), true);
});
