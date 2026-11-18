// check-all checks the repository it lives in, whatever that folder is called, and not a folder called "ample" next to it.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const script = new URL('./check-all.sh', import.meta.url);
const slash = (text) => text.split(String.fromCharCode(92)).join('/');   // the paths of bash have slashes
const hasBash = spawnSync('bash', ['--version'], { encoding: 'utf8' }).status === 0;

test('check-all.sh names its own root and uses nothing of a neighbouring folder called ample', { skip: !hasBash }, () => {
  const base = fs.mkdtempSync(path.join(os.tmpdir(), 'check-all-'));
  try {
    for (const folder of ['other-name', 'ample']) {
      fs.mkdirSync(path.join(base, folder, 'tools'), { recursive: true });
      fs.copyFileSync(script, path.join(base, folder, 'tools', 'check-all.sh'));
    }
    const run = spawnSync('bash', ['other-name/tools/check-all.sh', '--skip-tests'], { cwd: base, encoding: 'utf8', timeout: 60000 });
    const output = `${run.stdout}\n${run.stderr}`;
    const own = slash(path.join(base, 'other-name'));
    const decoy = slash(path.join(base, 'ample'));
    assert.ok(output.includes(`check-all: repository root`), 'it says which root it uses');
    assert.ok(output.includes(own.slice(own.indexOf('check-all-'))), 'the root is the folder of the script');
    assert.ok(!output.includes(decoy.slice(decoy.indexOf('check-all-'))), 'the decoy folder is not touched');
    assert.notEqual(run.status, 0, 'there is no solution in the test folder: it stops');
  } finally { fs.rmSync(base, { recursive: true, force: true }); }
});
