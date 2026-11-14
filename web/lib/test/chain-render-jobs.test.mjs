import test from 'node:test';
import assert from 'node:assert/strict';
import { bumpRevision, runRenderJob, createCompareGate } from '../chain-render-jobs.mjs';

const items = [{ id: 'a', inputs: [] }, { id: 'b', inputs: [{ from: 'a' }] }, { id: 'c', inputs: [] }];
const deferred = () => { let resolve, reject; const promise = new Promise((y, n) => { resolve = y; reject = n; }); return { promise, resolve, reject }; };
const tick = () => new Promise((r) => setImmediate(r));
function harness(list = items) {
  const log = { adopted: [], invalid: [], errors: [] }, revs = new Map(), jobs = [];
  const start = (id, render) => {
    const ticket = bumpRevision(revs, id);
    jobs.push(runRenderJob({ items: list, id, ticket, revs, alive: () => true, keyOf: (i) => `${i.id}@${ticket}`, render, adopt: (i, audio, key) => log.adopted.push([i.id, audio, key]), invalidate: (i, key) => log.invalid.push([i.id, key]), report: (e) => log.errors.push(e.message) }));
  };
  return { log, revs, start, all: () => Promise.all(jobs) };
}

test('an older render finishing after a newer one started is not adopted, and the newer one is', async () => {
  const h = harness(), first = deferred(), second = deferred();
  h.start('a', (i) => (i.id === 'a' ? first.promise : Promise.resolve('b-x')));
  h.start('a', (i) => (i.id === 'a' ? second.promise : Promise.resolve('b-y')));
  first.resolve('old'); await tick();
  assert.deepEqual(h.log.adopted, []);
  second.resolve('new'); await h.all();
  assert.deepEqual(h.log.adopted.map((x) => [x[0], x[1]]), [['a', 'new'], ['b', 'b-y']]);
});

test('the revision is taken when the render is scheduled: an older result is void before the newer job reaches the block', async () => {
  const h = harness(), first = deferred();
  h.start('a', (i) => (i.id === 'a' ? first.promise : Promise.resolve('b')));
  bumpRevision(h.revs, 'a');   // the second edit is scheduled (its 150 ms timer has not fired yet)
  first.resolve('old'); await h.all();
  assert.deepEqual(h.log.adopted, []);
});

test('the newest render failing after an older one was discarded invalidates the block, so it is retried', async () => {
  const h = harness(), first = deferred(), second = deferred();
  h.start('a', () => first.promise);
  h.start('a', () => second.promise);
  first.resolve('old'); second.reject(new Error('boom')); await h.all();
  assert.deepEqual(h.log.adopted, []);
  assert.deepEqual(h.log.invalid, [['a', 'a@2']]);
  assert.deepEqual(h.log.errors, ['boom']);
});

test('a failure of an older render is moot: nothing is invalidated', async () => {
  const h = harness(), first = deferred(), second = deferred();
  h.start('a', () => first.promise);
  h.start('a', () => second.promise);
  first.reject(new Error('old failure')); second.resolve('new'); await h.all();
  assert.deepEqual(h.log.invalid, []);
  assert.deepEqual(h.log.adopted.map((x) => x[0]), ['a', 'b']);
});

test('a failed block does not stop independent blocks, and blocks downstream of it are not rendered', async () => {
  const list = [{ id: 'a', inputs: [] }, { id: 'c', inputs: [] }, { id: 'b', inputs: [{ from: 'a' }] }];
  const h = harness(list), rendered = [];
  h.start('a', async (i) => { rendered.push(i.id); throw new Error('x'); });
  h.start('c', async (i) => { rendered.push(i.id); return i.id; });
  await h.all();
  assert.deepEqual(rendered, ['a', 'c']);
  assert.deepEqual(h.log.invalid.map((x) => x[0]), ['a']);
  assert.deepEqual(h.log.adopted.map((x) => x[0]), ['c']);
});

test('upstream edit while the downstream block renders: only the newest chain is adopted', async () => {
  const h = harness(), bFirst = deferred();
  let aCalls = 0;
  h.start('a', (i) => (i.id === 'a' ? Promise.resolve(`a${++aCalls}`) : bFirst.promise));
  await tick();   // job 1 adopted a1 and is rendering b
  h.start('a', (i) => Promise.resolve(i.id === 'a' ? `a${++aCalls}` : 'b2'));
  bFirst.resolve('b1'); await h.all();
  assert.deepEqual(h.log.adopted.map((x) => [x[0], x[1]]), [['a', 'a1'], ['a', 'a2'], ['b', 'b2']]);
});

test('A/B gate: on, off, on does not leave the comparison pending forever', () => {
  const gate = createCompareGate();
  const one = gate.begin();            // on
  assert.equal(gate.cancel(), true);   // off while loading
  const two = gate.begin();            // on again: a fresh load
  assert.ok(two && two !== one, 'the second on starts a fresh load');
  gate.finish(one);                    // the cancelled load ends: it must not touch the newer one
  assert.equal(gate.pending, true);
  assert.equal(gate.current(one), false);
  assert.equal(gate.current(two), true);
  gate.finish(two);
  assert.equal(gate.pending, false);
});

test('A/B gate: a load that fails or is cancelled always ends with nothing pending', () => {
  const gate = createCompareGate();
  const one = gate.begin(); gate.cancel(); gate.finish(one);
  assert.equal(gate.pending, false);
  const two = gate.begin(); gate.finish(two);
  assert.equal(gate.pending, false);
  assert.equal(gate.cancel(), false);
});
