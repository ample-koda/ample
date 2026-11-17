// WP5a of plan/diagnostics/PLAN.md: fail and require in the JS evaluator, the twin of FailRequireTests.cs
import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';

const THEME = '1:0:1:60:100 1:1:1:62:100';
const run = (expr, decls = []) => {
  const table = helperTableFrom(decls.map((text, i) => ({ text, line: i * 20 })));
  return evaluate(expr, { sources: { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } }, tempo: '120', helpers: table });
};

test('fail stops the evaluation with its message and gives no notes', () => {
  const r = run('f()', ['helper f() -> Notes {\n  fail "The preset is unknown"\n}']);
  assert.equal(r.errors.length, 1);
  assert.equal(r.errors[0].id, 'fail');
  assert.equal(r.errors[0].message, 'The preset is unknown');
  assert.ok(!r.notes);
});

test('require passes silently and fails with the text of the condition', () => {
  const helper = 'helper f(Int n) -> Notes {\n  require n   >  0\n  return theme.notes\n}';
  assert.deepEqual(run('f(1)', [helper]).errors, []);
  const r = run('f(0)', [helper]);
  assert.equal(r.errors.length, 1);
  assert.equal(r.errors[0].id, 'require');
  assert.equal(r.errors[0].message, 'Requirement failed: n > 0');
});

test('require takes a message and a hint', () => {
  const r = run('f(0)', ['helper f(Int n) -> Notes {\n  require(n > 0, "n must be positive", hint="Use 1 or more")\n  return theme.notes\n}']);
  assert.equal(r.errors[0].message, 'n must be positive');
  assert.equal(r.errors[0].suggestion, 'Use 1 or more');
});
