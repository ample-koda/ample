import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate, helperTableFrom, parseDeclaration } from '../note-pipeline.mjs';
import { limitsFrom } from '../note-lang.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const corpus = JSON.parse(fs.readFileSync(path.join(root, 'spec-tests', 'note-lang', 'core.json'), 'utf8'));
const theme = corpus.defaults.theme;
const fill = (s) => (s === undefined ? s : s.replaceAll('${T}', theme));

const run = (c, expr) => {
  const table = helperTableFrom((c.decls ?? []).map((text, i) => ({ text, line: i * 20 })));
  assert.deepEqual(table.issues, [], c.id + ': the declarations are fine');
  const clock = new Date(c.clock ?? corpus.defaults.clock);
  const sources = { theme: { events: evaluate(theme).value.events, span: 4 * 1_000_000 } };
  return evaluate(expr, { sources, tempo: corpus.defaults.tempo, helpers: table, clock: () => clock, capabilities: c.denied || c.envAllow ? { allowed: (name) => !(name in (c.denied ?? {})), why: (name) => (c.denied ?? {})[name], allowLists: () => (c.envAllow ? [c.envAllow] : []), nameAllowed: (cap, name) => cap !== 'environment' || !c.envAllow || c.envAllow.some((p) => (p === '*' ? true : p.startsWith('*') ? name.endsWith(p.slice(1)) : p.endsWith('*') ? name.startsWith(p.slice(0, -1)) : p === name)) } : null, environment: c.env ? (name) => c.env[name] : null, limits: { ...limitsFrom({}), ...(c.limits ?? {}) } });
};

for (const c of corpus.cases) {
  test(`note-lang corpus: ${c.id}`, () => {
    const result = run(c, fill(c.expr));
    assert.deepEqual(result.errors.map((e) => e.id), c.errors ?? [], c.id + ': errors');
    assert.deepEqual(result.warnings.map((w) => w.id), c.jsWarnings ?? c.warnings ?? [], c.id + ': warnings');
    if ((c.errors ?? []).length) { assert.equal(result.notes, null); return; }
    if (c.sameAs !== undefined) {
      const plain = evaluate(fill(c.sameAs), { sources: { theme: { events: evaluate(theme).value.events, span: 4 * 1_000_000 } }, tempo: corpus.defaults.tempo });
      assert.deepEqual(plain.errors, [], c.id + ': the plain expression is fine');
      assert.equal(result.notes, plain.notes, c.id + ': notes');
      assert.equal(result.span, plain.span, c.id + ': span');
    }
    if (c.count !== undefined) assert.equal(result.notes.split(' ').length, c.count, c.id + ': note count');
    if (c.dynamic !== undefined) assert.equal(result.dynamic, c.dynamic, c.id + ': dynamic');
  });
}

test('a declaration that does not parse is reported with its line', () => {
  const table = helperTableFrom([{ text: 'helper broken(Notes n -> Notes { n }', line: 7 }]);
  assert.equal(table.issues.length, 1);
  assert.equal(table.issues[0].line, 7);
  assert.equal(parseDeclaration('let pause = 2bars').ast.kind, 'constdecl');
  assert.equal(parseDeclaration('type Steps = Int | Float').ast.members.length, 2);
});

test('the effect of a helper follows its body and the helpers it calls', () => {
  const table = helperTableFrom([
    { text: 'dynamic helper now() -> Int { return currentTime().minute }', line: 0 },
    { text: 'helper usesNow() -> Int { now() + 1 }', line: 3 },
    { text: 'helper calm() -> Int { 1 }', line: 6 },
    { text: 'dynamic helper settled() -> Int bake as 5 { currentTime().second }', line: 9 },
    { text: 'helper usesSettled() -> Int { settled() }', line: 12 },
  ]);
  assert.equal(table.isDynamic('now'), true);
  assert.equal(table.isDynamic('usesNow'), true);
  assert.equal(table.isDynamic('calm'), false);
  assert.equal(table.isDynamic('settled'), false);   // a baked helper is pure for its callers
  assert.equal(table.isDynamic('usesSettled'), false);
});

test('limits come from the settings of a song and never pass the caps of the server', () => {
  assert.equal(limitsFrom({ 'limits.fuel': '500000' }).fuel, 500000);
  assert.equal(limitsFrom({ 'limits.fuel': '999999999999' }).fuel, 100000000);
  assert.equal(limitsFrom({}).stackDepth, 64);
});

test('the declarations of a song text are found in any order, with their lines', async () => {
  const { helperSourcesOf } = await import('../note-lang.mjs');
  const song = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\nlet pause = 2bars\n\nhelper a(Notes n) -> Notes {\n  n |> shift(pause)\n}\n\nmidi-clip c at 1 for auto {\n  notes a(\n    "1:0:1:60:100"\n  )\n}\n\ntype Steps = Int | Float\n\ndynamic helper b() -> Int { 1 }\n';
  const found = helperSourcesOf(song);
  assert.deepEqual(found.map((d) => d.line), [2, 4, 14, 16]);
  assert.match(found[1].text, /shift\(pause\)\n}$/);
  const table = helperTableFrom(found);
  assert.deepEqual(table.issues, []);
  assert.deepEqual(table.names().sort(), ['a', 'b']);
  assert.equal(table.isDynamic('b'), true);
});

test('two statements on one line need a ";" between them: "re turn 3" is a typo, not three statements', () => {
  const bad = helperTableFrom([{ text: 'helper f(Int p) -> Int {\n  if p > 1 { return 5 }\n  else { re turn 3 }\n}', line: 0 }]);
  assert.equal(bad.issues.length, 1);
  assert.equal(bad.issues[0].issue.id, 'syntax');
  assert.match(bad.issues[0].issue.message, /separated by ";"/);
  const good = helperTableFrom([
    { text: 'helper g(Int p) -> Int {\n  if p > 1 { return 5 } return 3\n}', line: 0 },
    { text: 'helper h(Int p) -> Int {\n  let a = 1; let b = 2; return a + b\n}', line: 20 },
    { text: 'helper k(Int p) -> Int {\n  let a = 1\n  let b = 2\n  return a + b\n}', line: 40 },
  ]);
  assert.deepEqual(good.issues, []);
});

// The static checks of a helper body: the same cases as src/Ample.Language (NoteLangStaticCorpusTests), so the browser and the server find the same things.
const staticCorpus = JSON.parse(fs.readFileSync(path.join(root, 'spec-tests', 'note-lang', 'static.json'), 'utf8'));
for (const c of staticCorpus.cases) {
  test(`static checks: ${c.id}`, () => {
    const table = helperTableFrom(c.decls.map((text, i) => ({ text, line: i * 20 })));
    const found = table.issues.map((x) => x.issue).filter((i) => staticCorpus.ids.includes(i.id)).map((i) => i.id + (i.strict ? '!' : '')).sort();
    assert.deepEqual(found, [...c.issues].sort());
  });
}

test('without the language server the browser still finds "re turn 3" and a name that is nothing, on the right lines', async () => {
  const { helperDiagnostics, helperSourcesOf } = await import('../note-lang.mjs');
  const song = [
    'composition "p" { ample 1  tempo 100  master-seed 1 }',
    '',
    'helper octaveFor(Int p) -> Octave {',
    '  if p % 2 == 0 { return 5 }',
    '  else { re turn 3 }',
    '}',
    '',
    'helper g(Int p) -> Int {',
    '  if p == 0 { return 1 }',
    '  else { return nothingHere }',
    '}',
    '',
  ].join('\n');
  const table = helperTableFrom(helperSourcesOf(song));
  const found = helperDiagnostics(song, table);
  const typo = found.find((d) => d.code === 'AMP1111');
  assert.ok(typo, 'the typo is a syntax error');
  assert.equal(typo.severity, 1);
  assert.equal(typo.range.start.line, 4);                       // the line of else { re turn 3 }
  assert.match(typo.message, /separated by ";"/);
  const unknown = found.find((d) => d.code === 'AMP2082');
  assert.ok(unknown);
  assert.equal(unknown.severity, 2);
  assert.equal(unknown.range.start.line, 9);                    // the line of the else with nothingHere
  assert.equal(song.split('\n')[9].slice(unknown.range.start.character, unknown.range.end.character), 'nothingHere');
});
