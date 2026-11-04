// Tests for validate-spec.mjs: the real spec passes, and deliberate breakage is reported. Run: node --test ample/tools/validate-spec.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const ample = path.join(here, '..');
const run = root => spawnSync(process.execPath, [path.join(here, 'validate-spec.mjs')], { env: { ...process.env, AMPLE_ROOT: root }, encoding: 'utf8' });
const copy = () => {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'ample-spec-'));
  for (const d of ['spec', 'spec-tests', path.join('docs', 'language')]) fs.cpSync(path.join(ample, d), path.join(tmp, d), { recursive: true });
  return tmp;
};

test('the real spec validates', () => {
  const r = run(ample);
  assert.equal(r.status, 0, r.stderr);
});

test('a duplicate diagnostic code is reported', () => {
  const tmp = copy(); const f = path.join(tmp, 'spec', 'diagnostics.json');
  const j = JSON.parse(fs.readFileSync(f, 'utf8')); j.diagnostics.push({ ...j.diagnostics[0] }); fs.writeFileSync(f, JSON.stringify(j));
  const r = run(tmp); assert.notEqual(r.status, 0); assert.match(r.stderr, /duplicate code/);
});

test('a code without an invalid case is reported', () => {
  const tmp = copy(); fs.rmSync(path.join(tmp, 'spec-tests', 'invalid', 'AMP2010-cycle.expected-diagnostics.json'));
  fs.rmSync(path.join(tmp, 'spec-tests', 'invalid', 'AMP2010-cycle.amplet'));
  const r = run(tmp); assert.notEqual(r.status, 0); assert.match(r.stderr, /AMP2010/);
});

test('grammar and prose that differ are reported', () => {
  const tmp = copy(); fs.appendFileSync(path.join(tmp, 'spec', 'grammar.ebnf'), 'extra = "x" ;\n');
  const r = run(tmp); assert.notEqual(r.status, 0); assert.match(r.stderr, /differs from spec\/grammar.ebnf/);
});

test('a dead link in the prose is reported', () => {
  const tmp = copy(); fs.appendFileSync(path.join(tmp, 'docs', 'language', '01-overview.md'), '\n[x](nope.md)\n');
  const r = run(tmp); assert.notEqual(r.status, 0); assert.match(r.stderr, /dead link nope.md/);
});

test('an unknown port type in a template is reported', () => {
  const tmp = copy(); const f = path.join(tmp, 'spec', 'types.json');
  const j = JSON.parse(fs.readFileSync(f, 'utf8')); j.types = j.types.filter(t => t.id !== 'midi'); fs.writeFileSync(f, JSON.stringify(j));
  const r = run(tmp); assert.notEqual(r.status, 0); assert.match(r.stderr, /unknown port type midi/);
});
