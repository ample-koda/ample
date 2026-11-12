import test from 'node:test';
import assert from 'node:assert/strict';
import { createCpuMonitor, heaviest, estimateLoad } from '../cpu-monitor.mjs';

test('a short spike does not bypass; a load kept over 90 percent does', () => {
  const monitor = createCpuMonitor();
  monitor.feed(0.2, 0);
  assert.equal(monitor.feed(1.5, 0.25).level, 'ok');   // smoothed
  let state;
  for (let t = 0.5; t <= 6; t += 0.25) state = monitor.feed(1.2, t);
  assert.equal(state.level, 'bypass');
});

test('between 70 and 90 percent only warns, and a low load recovers', () => {
  const monitor = createCpuMonitor();
  let state;
  for (let t = 0; t <= 5; t += 0.25) state = monitor.feed(0.8, t);
  assert.equal(state.level, 'warn');
  for (let t = 5; t <= 12; t += 0.25) state = monitor.feed(0.1, t);
  assert.equal(state.level, 'ok');
});

test('the heaviest block that is still on is picked, cheap ones are left alone', () => {
  const items = [{ id: 'a', tpl: 'eq' }, { id: 'b', tpl: 'reverb' }, { id: 'c', tpl: 'distortion' }];
  assert.equal(heaviest(items).id, 'b');
  items[1].bypassed = true;
  assert.equal(heaviest(items).id, 'c');
  assert.equal(heaviest([{ id: 'v', tpl: 'volume' }]), null);
});

test('the estimate grows with the chain', () => {
  assert.ok(estimateLoad([{ tpl: 'reverb' }, { tpl: 'reverb' }]) > estimateLoad([{ tpl: 'reverb' }]));
});
