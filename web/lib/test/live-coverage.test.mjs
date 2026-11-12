import test from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';

test('docs/clientside-preview/coverage.md is current (run node tools/gen-live-coverage.mjs after changing a template or web/lib/live-class.mjs)', () => {
  const run = spawnSync(process.execPath, ['tools/gen-live-coverage.mjs', '--check'], { cwd: new URL('../../../', import.meta.url), encoding: 'utf8' });
  assert.equal(run.status, 0, run.stderr);
});
