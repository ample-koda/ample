// node --test plugins/acestep/run-process.test.mjs: the child process runner is async, stops on timeout and cancel,
// and tells the failure kinds apart.
import test from 'node:test';
import assert from 'node:assert/strict';
import { runProcess } from './run-process.mjs';

const node = process.execPath;
const never = { isCancelled: () => false };
const hang = ['-e', 'setInterval(() => {}, 1000)'];

test('a hanging child is stopped at the time limit and the event loop keeps running', async () => {
  let ticks = 0;
  const timer = setInterval(() => { ticks++; }, 50);
  const began = Date.now();
  await assert.rejects(runProcess(node, hang, { ctx: never, timeoutMs: 800 }), e => e.kind === 'timeout');
  clearInterval(timer);
  assert.ok(Date.now() - began < 5000);
  assert.ok(ticks >= 5, 'the event loop was blocked (ticks: ' + ticks + ')');
});

test('cancel stops the child', async () => {
  let cancelled = false;
  setTimeout(() => { cancelled = true; }, 300);
  const began = Date.now();
  await assert.rejects(
    runProcess(node, hang, { ctx: { isCancelled: () => cancelled }, timeoutMs: 60000, pollMs: 100 }), e => e.kind === 'cancelled');
  assert.ok(Date.now() - began < 5000);
});

test('a missing program is a start error with the system code, an exit code is an exit error with stderr', async () => {
  await assert.rejects(
    runProcess('definitely-not-a-program-ample', [], { ctx: never, timeoutMs: 5000 }),
    e => e.kind === 'start' && e.code === 'ENOENT');
  await assert.rejects(
    runProcess(node, ['-e', 'console.error("No module named demucs"); process.exit(3)'], { ctx: never, timeoutMs: 5000 }),
    e => e.kind === 'exit' && e.exitCode === 3 && /No module named/.test(e.stderr));
  await runProcess(node, ['-e', ''], { ctx: never, timeoutMs: 5000 });
});
