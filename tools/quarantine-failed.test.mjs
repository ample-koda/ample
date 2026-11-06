import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { TAG, failedSpecs, listQuarantined, quarantine, release, retitle } from './quarantine-failed.mjs';

const SPEC = `import { test } from '@playwright/test';
test('first one works', async () => {});
test('second one fails: it "quotes" things', async () => {});
test.describe('group', () => {
  test(
    'third one fails too',
    async () => {});
});
`;
const report = {
  suites: [{ file: 'a.spec.ts', specs: [{ title: 'first one works', ok: true, file: 'a.spec.ts', line: 2 }, { title: 'second one fails: it "quotes" things', ok: false, file: 'a.spec.ts', line: 3 }],
    suites: [{ title: 'group', specs: [{ title: 'third one fails too', ok: false, file: 'a.spec.ts', line: 5 }] }] }],
};

const folder = () => { const d = fs.mkdtempSync(path.join(os.tmpdir(), 'quarantine-')); fs.writeFileSync(path.join(d, 'a.spec.ts'), SPEC); return d; };

test('the failed specs of a report are found, in nested groups too', () => {
  assert.deepEqual(failedSpecs(report).map((s) => s.line), [3, 5]);
});

test('a title gets the tag once, also when it is on the line after the call', () => {
  assert.match(retitle(SPEC, 3, (t) => `${t} ${TAG}`), /it "quotes" things @quarantine'/);
  assert.equal(retitle(SPEC.replace("works'", `works ${TAG}'`), 2, (t) => (t.includes(TAG) ? t : `${t} ${TAG}`)), null);
});

test('quarantine tags the failed tests, a second run changes nothing, and release takes the tag off again', () => {
  const dir = folder();
  const tagged = quarantine([report], { specDir: dir });
  assert.equal(tagged.length, 2);
  const after = fs.readFileSync(path.join(dir, 'a.spec.ts'), 'utf8');
  assert.match(after, /first one works', async/);   // the passing test is untouched
  assert.match(after, /'third one fails too @quarantine'/);
  assert.equal(listQuarantined(dir).length, 2);
  assert.equal(quarantine([report], { specDir: dir }).length, 0);
  assert.equal(release('third one', { specDir: dir }).length, 1);
  assert.equal(listQuarantined(dir).length, 1);
  assert.equal(fs.readFileSync(path.join(dir, 'a.spec.ts'), 'utf8').includes('third one fails too\''), true);
});

test('a dry run writes nothing', () => {
  const dir = folder();
  assert.equal(quarantine([report], { dryRun: true, specDir: dir }).length, 2);
  assert.equal(fs.readFileSync(path.join(dir, 'a.spec.ts'), 'utf8'), SPEC);
});
