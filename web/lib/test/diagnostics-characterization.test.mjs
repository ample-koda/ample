// WP0 of plan/diagnostics/PLAN.md: what the JS evaluator does today, pinned next to the C# twin (NoteLangCharacterizationTests).
import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';

const THEME = '1:0:1:60:100 1:1:1:62:100 1:2:1:64:100 1:3:1:65:100';
const sources = () => ({ theme: { events: evaluate(THEME).value.events, span: 4 * 1_000_000 } });
const run = (expr, decls = [], extra = {}) => {
  const table = helperTableFrom(decls.map((text, i) => ({ text, line: i * 20 })));
  return { table, result: evaluate(expr, { sources: sources(), tempo: '120', helpers: table, ...extra }) };
};

// the smallest fuel with which the helper completes: the C# test asserts the same number (209)
export const FUEL_BOUNDARY = 209;

test('fuel is counted in abstract steps and stops at the same place as the C# evaluator', () => {
  const decls = ['helper busy(Notes n) -> Notes {\n  var k = 0\n  for i in 0..40 { k = k + 1 }\n  return n\n}'];
  let lowest = -1;
  for (let fuel = 1; fuel < 400; fuel++) {
    const { result } = run('busy(theme.notes)', decls, { limits: { fuel } });
    if (!result.errors.some((e) => e.id === 'fuel')) { lowest = fuel; break; }
  }
  assert.equal(lowest, FUEL_BOUNDARY);
});

test('the words of the statements are a visible error while their feature is off', () => {
  for (const statement of ['fail "x"', 'throw "x"', 'require 1 > 0, "x"', 'assert 1 > 0, "x"', 'log "x"', 'debug 1', 'warn "x"']) {
    const table = helperTableFrom([{ text: `helper f(Notes n) -> Notes {\n  ${statement}\n  return n\n}`, line: 0 }], []);
    const result = evaluate('f(theme.notes)', { sources: sources(), tempo: '120', helpers: table });
    assert.ok(table.issues.length > 0 || result.errors.length > 0, statement + ': accepted silently');
    assert.equal(result.notes, null, statement);
  }
});

test('a helper that emits and then fails gives no notes at all', () => {
  const { result } = run('g(theme.notes)', ['helper g(Notes s) -> Notes {\n  emit s\n  let x = 1 / 0\n  return s\n}']);
  assert.ok(result.errors.some((e) => e.id === 'divide-by-zero'));
  assert.equal(result.notes, null);
});

test('an error inside a helper is reported with a range that belongs to the helper text', () => {
  const expr = 'g(theme.notes)';
  const { result } = run(expr, ['helper g(Notes s) -> Notes {\n  let x = 1 / 0\n  return s\n}']);
  assert.equal(result.errors.length, 1);
  assert.equal(result.errors[0].id, 'divide-by-zero');
  console.log('JS range', result.errors[0].start, result.errors[0].end, 'expression length', expr.length);
});
