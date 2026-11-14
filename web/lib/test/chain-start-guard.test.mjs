import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { failedAlready } from '../chain-start-guard.mjs';

test('the same target in the same song state is not tried again after it failed', () => {
  assert.equal(failedAlready({ target: 'a', signature: 's1' }, 'a', 's1'), true);
});
test('a changed song state, another target, or no failure at all is tried', () => {
  assert.equal(failedAlready({ target: 'a', signature: 's1' }, 'a', 's2'), false);
  assert.equal(failedAlready({ target: 'a', signature: 's1' }, 'b', 's1'), false);
  assert.equal(failedAlready(null, 'a', 's1'), false);
});
test('live-chain.js asks the guard before restarting, records the failure, and clears it on success', () => {
  const src = fs.readFileSync(new URL('../../app/live-chain.js', import.meta.url), 'utf8');
  assert.ok(src.includes('failedAlready(failedStart, target,'));
  assert.ok(src.includes('failedStart = { target, signature: mine.signature }'));
  assert.ok(src.includes('failedStart = null; pendingTarget = null;'));
});
test('the graph is built before the swap (a failing build leaves the old chain alone); Stop and a project switch bump the generation', () => {
  const src = fs.readFileSync(new URL('../../app/live-chain.js', import.meta.url), 'utf8');
  const start = src.slice(src.indexOf('async function startSession('), src.indexOf('const effective ='));
  assert.ok(start.indexOf('build(mine)') > -1 && start.indexOf('build(mine)') < start.indexOf('stopChain({ quiet: true })'));
  assert.ok(start.indexOf('build(mine)') < start.indexOf('session = mine'));
  const stop = src.slice(src.indexOf('export function stopChain('), src.indexOf('export const chainActive'));
  assert.ok(stop.indexOf('startGeneration++') > -1 && stop.indexOf('startGeneration++') < stop.indexOf('if (!session) return'));
  assert.ok(src.includes("document.addEventListener('ample:open', () => stopChain())"));
});
