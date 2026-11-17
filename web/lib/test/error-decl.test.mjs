// WP5a of plan/diagnostics/PLAN.md: error declarations in the JS evaluator (the shared corpus covers the behaviour; this file covers the static checks)
import test from 'node:test';
import assert from 'node:assert/strict';
import { helperTableFrom } from '../note-pipeline.mjs';

const issuesOf = (...decls) => helperTableFrom(decls.map((text, i) => ({ text, line: i * 20 }))).issues;

test('a wrong error declaration is reported when the file is read', () => {
  for (const declaration of ['error A extends Nope { Int n }', 'error log { Int n }', 'error Error { Int n }', 'error B { Int message }', 'error C { Int n, String n }']) {
    assert.ok(issuesOf(declaration).length > 0, declaration);
  }
});

test('errors that extend each other in a circle are reported', () => {
  assert.ok(issuesOf('error A extends B { }', 'error B extends A { }').some((i) => i.issue.id === 'error-decl'));
});

test('a good declaration, also with a dynamic field of a generic type, has no issue', () => {
  assert.deepEqual(issuesOf('error PlainError { }', 'error ListError extends PlainError { dynamic List<Int> items }'), []);
});

const effectOf = (helper, ...declarations) => {
  const table = helperTableFrom([...declarations, ...helper.split('\n\n')].map((text, i) => ({ text, line: i * 20 })));
  return table.helper('read')?.effect;
};
const VARIATION = 'error VariationError extends Error { dynamic Int variation }';
const OTHERS = ['error SubVariation extends VariationError { }', 'error PlainOne { Int variation }'];

test('a field of another type with the same name does not make a helper dynamic', () => {
  for (const helper of [
    'helper read(Record settings) -> Int {\n  return settings.variation\n}',
    'helper read(Record settings) -> Int {\n  let s = settings\n  return s.variation\n}',
    'helper read() -> Int {\n  let s = {variation: 3}\n  return s.variation\n}',
    'helper read(PlainOne e) -> Int {\n  return e.variation\n}',
    'helper read() -> Int {\n  try { throw PlainOne("x", variation=1) } catch (PlainOne e) { return e.variation }\n  return 0\n}',
  ]) assert.equal(effectOf(helper, VARIATION, ...OTHERS), 'pure', helper);
});

test('a read of the declared dynamic field still makes a helper dynamic', () => {
  for (const helper of [
    'helper read(VariationError e) -> Int {\n  return e.variation\n}',
    'helper read(SubVariation e) -> Int {\n  return e.variation\n}',
    'helper read(Error e) -> Int {\n  return e.variation\n}',
    'helper read(VariationError e) -> Int {\n  let alias = e\n  return alias.variation\n}',
    'helper read() -> Int {\n  try { throw VariationError("x", variation=1) } catch (e) { return e.variation }\n  return 0\n}',
    'helper make() -> VariationError {\n  return VariationError("x", variation=1)\n}\n\nhelper read() -> Int {\n  return make().variation\n}',
  ]) assert.equal(effectOf(helper, VARIATION, ...OTHERS), 'dynamic', helper);
});
