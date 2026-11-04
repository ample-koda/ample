import test from 'node:test';
import assert from 'node:assert/strict';
import { pause } from '../yield-slice.mjs';

test('pause yields to the page only when a slice of work is used up', async () => {
  await pause();   // the first call opens a slice (it may yield)
  let yielded = 0;
  for (let i = 0; i < 2000; i++) if (pause() !== undefined) yielded++;
  assert.ok(yielded < 5, `a tight loop must not yield on every call (yielded ${yielded} times)`);
  const start = performance.now();
  while (performance.now() - start < 20);   // use up a slice
  const handed = pause();
  assert.ok(handed instanceof Promise, 'after a slice of work it hands control back');
  await handed;
});
