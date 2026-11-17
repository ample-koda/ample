// WP5a of plan/diagnostics/PLAN.md: throw, error values and try/catch/finally in the JS evaluator (the shared corpus covers the behaviour; this file covers the statics)
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';

const THEME = '1:0:1:60:100 1:1:1:62:100';
const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');

test('a finally block cannot return, emit or throw', () => {
  for (const statement of ['return theme.notes', 'emit theme.notes', 'throw "y"']) {
    const table = helperTableFrom([{ text: 'helper f() -> Notes {\n  try { let a = 1 } finally { ' + statement + ' }\n  return theme.notes\n}', line: 0 }]);
    assert.ok(table.issues.some((i) => i.issue.id === 'finally-flow'), statement);
  }
});

test('try needs a catch or a finally', () => {
  const table = helperTableFrom([{ text: 'helper f() -> Notes {\n  try { let a = 1 }\n  return theme.notes\n}', line: 0 }]);
  assert.ok(table.issues.length > 0);
});

test('a path that ends in throw needs no value (it ends the evaluation, like fail)', () => {
  const table = helperTableFrom([{ text: 'helper f(Int p) -> Notes {\n  if p == 0 { return theme.notes }\n  throw "no such p"\n}', line: 0 }]);
  assert.ok(!table.issues.some((i) => i.issue.id === 'no-value'));
});

test('a helper that ends in try needs a value on every path', () => {
  const ok = helperTableFrom([{ text: 'helper f() -> Notes {\n  try { return theme.notes } catch (e) { return theme.notes }\n}', line: 0 }]);
  assert.ok(!ok.issues.some((i) => i.issue.id === 'no-value'));
  const bad = helperTableFrom([{ text: 'helper g() -> Notes {\n  try { return theme.notes } catch (e) { let a = 1 }\n}', line: 0 }]);
  assert.ok(bad.issues.some((i) => i.issue.id === 'no-value'));
});

test('finally runs after an uncaught throw and an unhandled fail but the failure still ends the run', () => {
  for (const [word, id] of [['throw "x"', 'throw'], ['fail "x"', 'fail']]) {
    const table = helperTableFrom([{ text: 'helper f() -> Notes {\n  try { ' + word + ' } finally { let a = 1 }\n  return theme.notes\n}', line: 0 }]);
    const r = evaluate('f()', { sources: { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } }, tempo: '120', helpers: table });
    assert.ok(r.errors.some((e) => e.id === id), word);
  }
});

test('the catchable failures of the runtime are exactly the T1 rows of the failure-class table', () => {
  const table = JSON.parse(fs.readFileSync(path.join(root, 'spec', 'failure-classes.json'), 'utf8'));
  const t1 = table.failures.filter((f) => f.class === 'T1').map((f) => f.id).sort();
  assert.deepEqual(t1, ['conversion', 'divide-by-zero', 'require', 'throw', 'unknown-field']);
});

const LOOP = 'repeat 1000 { let a = 1 }';   // about 3,100 steps: alone it fits the reserve of 5,000, two do not
const reserveRun = (body) => {
  const table = helperTableFrom([{ text: 'helper f() -> Notes {\n  ' + body + '\n  return theme.notes\n}', line: 0 }]);
  const sources = { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } };
  const r = evaluate('f()', { sources, tempo: '120', helpers: table });
  return { r, notices: (r.warnings ?? []).filter((w) => w.id === 'unwind-reserve').length };
};
const failedWithX = (r) => r.errors.some((e) => e.id === 'fail' && e.message === 'x');

test('one finally that fits the unwind reserve gives no notice', () => {
  const { r, notices } = reserveRun('try { fail "x" } finally { ' + LOOP + ' }');
  assert.equal(notices, 0);
  assert.ok(failedWithX(r));
});

test('nested finally blocks share one unwind reserve and the notice is given once', () => {
  const { r, notices } = reserveRun('try { try { fail "x" } finally { ' + LOOP + ' } } finally { ' + LOOP + ' }');
  assert.equal(notices, 1);
  assert.ok(failedWithX(r));
});

test('an exhausted reserve skips every remaining finally', () => {
  const { r, notices } = reserveRun('try { try { try { fail "x" } finally { ' + LOOP + ' } } finally { ' + LOOP + ' } } finally { log "outer" }');
  assert.equal(notices, 1);
  assert.ok(failedWithX(r));
});

test('a try inside a finally does not replenish the reserve and the original failure stays primary', () => {
  const { r, notices } = reserveRun('try { try { fail "x" } finally { try { fail "y" } finally { ' + LOOP + ' } } } finally { ' + LOOP + ' }');
  assert.equal(notices, 1);
  assert.ok(failedWithX(r));
  assert.ok(!r.errors.some((e) => e.message === 'y'));
});
