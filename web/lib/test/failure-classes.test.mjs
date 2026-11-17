// WP2 of plan/diagnostics/PLAN.md: every failure id that the JS evaluator raises has a class in the table that the C# evaluator reads too (spec/failure-classes.json).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const table = JSON.parse(fs.readFileSync(path.join(root, 'spec', 'failure-classes.json'), 'utf8'));
const byId = new Map(table.failures.map((f) => [f.id, f]));

test('the table has unique ids and a stable code for every catchable failure', () => {
  assert.equal(byId.size, table.failures.length);
  for (const f of table.failures) {
    assert.ok(['T1', 'T2', 'T3', 'S'].includes(f.class), f.id);
    if (f.class === 'T1') assert.ok(f.code && /^[A-Z][A-Z0-9_]*$/.test(f.code), f.id + ': a catchable failure has a code');
    else assert.equal(f.code, undefined, f.id + ': only a catchable failure has a code');
  }
});

test('every failure id that the JS evaluator raises is in the table and marked for js', () => {
  const source = fs.readFileSync(path.join(root, 'web', 'lib', 'note-lang.mjs'), 'utf8');
  const ids = new Set([...source.matchAll(/new Failure\('([a-z-]+)'/g)].map((m) => m[1]));
  assert.ok(ids.has('divide-by-zero'));
  for (const id of ids) {
    const row = byId.get(id);
    assert.ok(row, `the failure id ${id} is raised by the JS evaluator but has no class in spec/failure-classes.json`);
    assert.ok(row.runtimes.includes('js'), `${id} must list js among its runtimes`);
  }
});

test('the failures that only exist in C# are the time and cancellation ones, which JS does not have', () => {
  const csharpOnly = table.failures.filter((f) => !f.runtimes.includes('js')).map((f) => f.id).sort();
  assert.deepEqual(csharpOnly.filter((id) => ['cap-time', 'cancelled'].includes(id)), ['cancelled', 'cap-time']);
});
