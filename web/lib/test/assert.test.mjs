// WP5b of plan/diagnostics/PLAN.md: assert in the JS evaluator and the delivery of its switch to the browser (the shared corpus covers the behaviour; this file covers the rest)
import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';
import { assertionsOf } from '../observation-config.mjs';

const THEME = '1:0:1:60:100 1:1:1:62:100';
const run = (helper, assertions) => {
  const table = helperTableFrom([{ text: helper, line: 0 }]);
  return evaluate('f(0)', { sources: { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } }, tempo: '120', helpers: table, assertions });
};
const helper = (body) => 'helper f(Int n) -> Notes {\n  ' + body + '\n  return theme.notes\n}';

test('a failed assertion says its message or the text of its condition', () => {
  assert.equal(run(helper('assert n > 0, "n must be positive"'), true).errors[0].message, 'n must be positive');
  assert.equal(run(helper('assert n   >  0'), true).errors[0].message, 'Assertion failed: n > 0');
});

test('an assertion that cannot be evaluated within its budget fails with its own id', () => {
  const spin = 'helper spin(Int n) -> Bool {\n  var i = 0\n  while i < 9000 max 9000 { i = i + 1 }\n  return n > 0\n}';
  const table = helperTableFrom([{ text: spin, line: 0 }, { text: helper('repeat 12 { assert spin(n) }'), line: 10 }]);
  const r = evaluate('f(1)', { sources: { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } }, tempo: '120', helpers: table, assertions: true });
  assert.ok(r.errors.some((e) => e.id === 'assert-unavailable'));
});

test('a clock read or a dynamic helper in a condition fails the assertion', () => {
  assert.ok(run(helper('assert currentTime().hour > 0'), true).errors.some((e) => e.id === 'assert'));
});

test('the statement costs the same main steps on or off', () => {
  const lowest = (on) => {
    for (let fuel = 5; fuel < 200; fuel++) {
      const table = helperTableFrom([{ text: helper('assert n > 0 && n < 100, "range"'), line: 0 }]);
      const r = evaluate('f(3)', { sources: { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } }, tempo: '120', helpers: table, assertions: on, limits: { fuel } });
      if (!r.errors.some((e) => e.id === 'fuel')) return fuel;
    }
    return -1;
  };
  assert.equal(lowest(false), lowest(true));
  assert.ok(lowest(false) > 0);
});

test('the switch of the browser comes from the song, the instance and the session in that order', () => {
  const song = 'composition "x" { ample 1  tempo 100  master-seed 1 }\nsettings { diagnostics.assertions on }\n';
  assert.equal(assertionsOf('composition "x" { ample 1  tempo 100  master-seed 1 }'), false);
  assert.equal(assertionsOf(song), true);
  assert.equal(assertionsOf(song, { session: false }), false);
  assert.equal(assertionsOf('', { directives: [{ name: 'diagnostics.assertions', value: 'on', line: 4 }] }), true);
  assert.equal(assertionsOf(song, { directives: [{ name: 'diagnostics.assertions', value: 'off', line: 4 }] }), false);
  assert.equal(assertionsOf('', { session: true }), true);
});

test('a passing assertion leaves the notes, the dynamic status, the taint and the lists of the run as they were', () => {
  const phase = 'dynamic helper phase() -> Int { return currentTime().minute % 8 }';
  const f = 'dynamic helper f(Int n) -> Notes {\n  let p = phase()\n  assert n >= 0, "n"\n  return theme.notes |> transpose(p)\n}';
  const sources = { theme: { events: evaluate(THEME).value.events, span: 2 * 1_000_000 } };
  const helpers = helperTableFrom([{ text: phase, line: 0 }, { text: f, line: 10 }]);
  const result = (assertions) => evaluate('f(1)', { sources, tempo: '120', clock: () => new Date('2026-10-26T12:05:00'), helpers, assertions });
  const off = result(false), on = result(true);
  assert.deepEqual(on.errors, []);
  assert.equal(on.notes, off.notes);
  assert.equal(on.dynamic, off.dynamic);
  assert.equal(on.tainted, off.tainted);
  assert.deepEqual(on.warnings, off.warnings);
});
