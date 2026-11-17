// A value read with environment() is a secret in the JavaScript twin too (the twin of TextBuildingTests in C#): no error message of +, String or
// sprintf repeats it, also not after it was formatted, and a secret format never shows its placeholders.
import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluate, helperTableFrom } from '../note-pipeline.mjs';

const PLANTED = 'planted-credential-value';
const theme = '1:0:1:60:100 1:1:1:62:100';

const run = (body, value = PLANTED) => {
  const table = helperTableFrom([{ text: `dynamic helper g() -> Int {\n  let key = environment("AMPLE_TEST")\n${body}\n}`, line: 0 }]);
  assert.deepEqual(table.issues.filter((i) => i.issue.id !== 'unused-variable'), []);
  return evaluate(`${theme} |> transpose(g())`, { tempo: 120, helpers: table, environment: () => value });
};
const message = (body, value) => {
  const r = run(body, value);
  assert.equal(r.errors.length, 1, JSON.stringify(r.errors));
  return r.errors[0];
};

for (const [name, body, shown = true] of [
  ['%d with a secret', '  return count(sprintf("%d", key))'],
  ['%f with a secret', '  return count(sprintf("%f", key))'],
  ['%d with text made from a secret', '  return count(sprintf("%d", "k=" + key))'],
  ['%f with String of a secret', '  return count(sprintf("%.2f", String(key)))'],
  ['+ of a secret and a Bool', '  return count(key + true)', false],   // the message names the Bool, not the text
  ['+ of made text and a list', '  return count(sprintf("%s!", key) + [1])', false],
  ['+ of a record holding a secret and a text', '  return count({k: key} + "x")'],
  ['minus on a secret', '  return count(key - 1)'],
  ['Int of a secret', '  return Int(key)'],
]) {
  test(`a wrong ${name} never shows the secret in its message`, () => {
    const e = message(body);
    assert.ok(!e.message.includes(PLANTED), e.message);
    if (shown) assert.ok(e.message.includes('[redacted]'), e.message);
  });
}

test('a secret format never shows its placeholders in a message', () => {
  for (const format of ['%q-' + PLANTED, '%.f' + PLANTED, '%100d' + PLANTED, '%d and %d ' + PLANTED]) {
    const e = message('  return count(sprintf(key, 1))', format);
    assert.equal(e.id, 'bad-argument');
    assert.ok(!e.message.includes(PLANTED) && !e.message.includes('"%'), e.message);
  }
});

test('the same mistakes with plain values still say what was wrong', () => {
  assert.match(message('  return count(sprintf("%d", 1.5))').message, /%d takes an Int, not 1\.5 \(Float\)/);
  assert.match(message('  return count(sprintf("%q", 1))').message, /"%q" is not supported/);
});

test('String takes exactly one value', () => {
  for (const body of ['  return count(String(1, 2))', '  return count(String())', '  return count(String(value=1))']) assert.equal(message(body).id, 'bad-argument', body);
});
