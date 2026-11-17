// The syntax gate (plan/diagnostics/PLAN.md, D17) in the JS evaluator: same shipped set as C#, same AMP6020 for a feature that is off.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { helperTableFrom, parseExpression, SYNTAX_FEATURES } from '../note-pipeline.mjs';
import { helperDiagnostics, helperSourcesOf } from '../note-lang.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const gate = JSON.parse(fs.readFileSync(path.join(root, 'spec-tests', 'note-lang', 'feature-gate.json'), 'utf8'));

test('the shipped syntax features are the shared set (C# compares itself with the same file)', () => {
  assert.deepEqual([...SYNTAX_FEATURES].sort(), [...gate.shipped].sort());
});

test('behind the feature gate both observation forms parse into the same statement', () => {
  const parse = (statement) => parseExpression(
    'helper sample() -> Int { ' + statement + '; return 7 }',
    { declaration: true, syntaxFeatures: ['Observe', 'Record'] },
  );
  const call = parse('debug("value", context={amount: 3})');
  const bare = parse('debug "value", context={amount: 3}');
  for (const result of [call, bare]) {
    assert.deepEqual(result.errors, []);
    const [event] = result.ast.body.statements;
    assert.equal(event.kind, 'observe');
    assert.equal(event.name, 'debug');
    assert.equal(event.named[0].name, 'context');
    assert.equal(event.named[0].value.kind, 'record');
  }
  assert.equal(parse('debug (1 + 2) * 3').ast.body.statements[0].positional[0].kind, 'binary');
  assert.equal(parse('debug("value", context={}, context={})').errors[0].id, 'syntax');
});

for (const c of gate.cases) {
  test(`a feature that is off is reported as feature-off and gives no helper: ${c.id}`, () => {
    const text = `helper f(Notes n, Int voices = 2) -> Notes {\n${c.body}\n  return n\n}`;
    const table = helperTableFrom([{ text, line: 0 }], []);   // every feature is shipped: an empty gate shows the feature that is off
    assert.equal(table.issues.length, 1, c.id + ': one issue');
    assert.equal(table.issues[0].issue?.id ?? table.issues[0].id, 'feature-off', c.id);
  });
}

test('the editor diagnostics name it AMP6020 as an error', () => {
  const text = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\nhelper f() -> Int {\n  assert true\n  return 1\n}\n';
  const diagnostics = helperDiagnostics(text, helperTableFrom(helperSourcesOf(text), []));
  const d = diagnostics.find((x) => x.code === 'AMP6020');
  assert.ok(d, 'AMP6020 present');
  assert.equal(d.severity, 1);
});

test('an ordinary variable that looks like a word is not a statement', () => {
  const table = helperTableFrom([{ text: 'helper f(Notes n) -> Notes {\n  var log = 1\n  log = log + 1\n  return n\n}', line: 0 }]);
  assert.deepEqual(table.issues.map((i) => i.issue?.id ?? i.id).filter((id) => id === 'feature-off' || id === 'syntax'), []);
});

test('a helper named like a reserved word gets AMP6016 as a warning and still works', () => {
  const text = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\nhelper log(Int n) -> Int {\n  return n + 1\n}\n';
  const table = helperTableFrom(helperSourcesOf(text));
  const d = helperDiagnostics(text, table).find((x) => x.code === 'AMP6016');
  assert.ok(d, 'AMP6016 present');
  assert.equal(d.severity, 2);
});
