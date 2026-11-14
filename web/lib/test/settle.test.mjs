import test from 'node:test';
import assert from 'node:assert/strict';
import { abortError, abortable, eventOrCancel } from '../settle.mjs';

const never = () => new Promise(() => {});

/** A target that records its listeners, so the tests can see that none are left. */
function fakeTarget() {
  const listeners = new Map();
  return {
    addEventListener(name, fn) { if (!listeners.has(name)) listeners.set(name, new Set()); listeners.get(name).add(fn); },
    removeEventListener(name, fn) { listeners.get(name)?.delete(fn); },
    emit(name) { for (const fn of [...(listeners.get(name) ?? [])]) fn(); },
    count() { let n = 0; for (const set of listeners.values()) n += set.size; return n; },
  };
}

test('abortable gives the result when the promise answers first', async () => {
  const controller = new AbortController();
  assert.equal(await abortable(Promise.resolve(42), controller.signal), 42);
});

test('abortable passes the failure of the promise on', async () => {
  const controller = new AbortController();
  await assert.rejects(abortable(Promise.reject(new Error('boom')), controller.signal), /boom/);
});

test('abortable ends at once when the signal is aborted, without waiting for the promise (issue 70)', async () => {
  const controller = new AbortController();
  const waiting = abortable(never(), controller.signal);
  controller.abort();
  await assert.rejects(waiting, (e) => e.name === 'AbortError');
});

test('abortable with a signal that is aborted already rejects at once and hides the late failure of the promise', async () => {
  const controller = new AbortController();
  controller.abort();
  const late = Promise.reject(new Error('late failure'));
  await assert.rejects(abortable(late, controller.signal), (e) => e.name === 'AbortError');
});

test('a late answer after the abort has no effect', async () => {
  const controller = new AbortController();
  let answer;
  const slow = new Promise((resolve) => { answer = resolve; });
  const waiting = abortable(slow, controller.signal);
  controller.abort();
  await assert.rejects(waiting, (e) => e.name === 'AbortError');
  answer('late');   // must not throw or change anything
  await Promise.resolve();
});

test('without a signal abortable is the promise itself', async () => {
  assert.equal(await abortable(Promise.resolve('x'), undefined), 'x');
});

test('abortError has the name of a cancelled operation', () => {
  assert.equal(abortError().name, 'AbortError');
  assert.equal(abortError('stopped').message, 'stopped');
});

test('eventOrCancel resolves true on the ok event and removes its listeners', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata'], ['error']);
  assert.equal(target.count(), 2);
  target.emit('loadedmetadata');
  assert.equal(await wait.promise, true);
  assert.equal(target.count(), 0);
});

test('eventOrCancel rejects with the made error on a fail event and removes its listeners', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata'], ['error'], () => new Error('could not load'));
  target.emit('error');
  await assert.rejects(wait.promise, /could not load/);
  assert.equal(target.count(), 0);
});

test('cancel settles the wait with false even when no event ever comes (issue 72)', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata'], ['error']);
  wait.cancel();
  assert.equal(await wait.promise, false);
  assert.equal(target.count(), 0);
});

test('an event after the cancel changes nothing, and a second cancel is harmless', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata'], ['error']);
  wait.cancel();
  target.emit('loadedmetadata');
  wait.cancel();
  assert.equal(await wait.promise, false);
});

test('a cancel after the event keeps the answer that was given', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata'], ['error']);
  target.emit('loadedmetadata');
  wait.cancel();
  assert.equal(await wait.promise, true);
});

test('several event names can be listed for either outcome', async () => {
  const target = fakeTarget();
  const wait = eventOrCancel(target, ['loadedmetadata', 'canplay'], ['error', 'abort']);
  assert.equal(target.count(), 4);
  target.emit('canplay');
  assert.equal(await wait.promise, true);
  assert.equal(target.count(), 0);
});
