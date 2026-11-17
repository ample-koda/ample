// WP4a of plan/diagnostics/PLAN.md: records in the JS evaluator, the twin of RecordTests.cs
import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';

const THEME = '1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100';
const PHASE = 'dynamic helper phase() -> Int { return currentTime().minute % 8 }';
const run = (expr, decls = []) => {
  const table = helperTableFrom(decls.map((text, i) => ({ text, line: i * 20 })));
  return evaluate(expr, { sources: { theme: { events: evaluate(THEME).value.events, span: 4 * 1_000_000 } }, tempo: '120', helpers: table, clock: () => new Date('2026-11-16T10:05:00') });
};
const good = (expr, decls) => { const r = run(expr, decls); assert.deepEqual(r.errors, []); return r; };

test('a record literal has fields that are read with a dot', () => {
  assert.equal(good('f()', ['helper f() -> Notes {\n  let r = {steps: 3, label: "x", on: true}\n  return theme.notes |> transpose(r.steps)\n}']).notes, good('theme.notes |> transpose(3)').notes);
});

test('records nest and hold expressions and lists', () => {
  const r = good('f(2)', ['helper f(Int n) -> Notes {\n  let r = {inner: {a: n * 2, b: [n, 5]}, "quoted key": 1}\n  return theme.notes |> transpose(r.inner.a + at(r.inner.b, 1))\n}']);
  assert.equal(r.notes, good('theme.notes |> transpose(9)').notes);
});

test('a record is a typed value and is can ask for it', () => {
  const r = good('go(theme.notes, {steps: 4})', ['helper pick(Record r) -> Int { return r.steps }', 'helper go(Notes n, Record r) -> Notes { return n |> transpose(pick(r)) }']);
  assert.equal(r.notes, good('theme.notes |> transpose(4)').notes);
  assert.equal(good('f()', ['helper f() -> Notes {\n  let r = {}\n  return theme.notes |> transpose(r is Record ? 2 : 3)\n}']).notes, good('theme.notes |> transpose(2)').notes);
});

test('an unknown field is a clear error and a record cannot be given to an operator', () => {
  const r = run('f()', ['helper f() -> Notes {\n  let r = {a: 1, b: 2}\n  return theme.notes |> transpose(r.c)\n}']);
  assert.equal(r.errors.length, 1);
  assert.equal(r.errors[0].id, 'unknown-field');
  assert.match(r.errors[0].message, /it has a, b/);
  assert.ok(run('f()', ['helper f() -> Notes {\n  let r = {a: 1}\n  return theme.notes |> transpose(r)\n}']).errors.some((e) => e.id === 'type-mismatch'));
});

test('a field keeps its own taint and a sibling field stays plain', () => {
  const go = 'helper go(Record r, Bool dynamicField) -> Notes { return theme.notes |> transpose(dynamicField ? r.t : r.n) }';
  const dynamicField = good('go({t: phase(), n: 3}, true)', [PHASE, go]);
  assert.equal(dynamicField.dynamic, true);
  assert.equal(dynamicField.tainted, true);
  const plain = good('go({t: phase(), n: 3}, false)', [PHASE, go]);
  assert.equal(plain.dynamic, true);
  assert.equal(plain.tainted, false);
});
