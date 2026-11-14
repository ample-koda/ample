// A guard for the instance-file patterns of CloseRunning.cs (the MSI custom action cannot run outside Windows Installer):
// the verbatim C# patterns are read from the source and tried, as JavaScript patterns of the same syntax,
// against the JSON that the instance registry writes (camelCase, indented).
// Run: node --test install/msi/close-running.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('./CustomActions/CloseRunning.cs', import.meta.url), 'utf8');
const body = source.slice(source.indexOf('internal static bool TryReadInstance'));
const pattern = (name) => {
  const m = new RegExp(String.raw`Match ${name} = Regex\.Match\([^,]+, @"((?:[^"]|"")*)"`).exec(body);
  assert.ok(m, `pattern ${name} not found`);
  return new RegExp(m[1].replaceAll('""', '"'), 'i');
};

const json = JSON.stringify({
  schema: 1, projectId: '0198', name: 'song', folder: 'C:\p', pid: 4321, processStart: '2026-11-14T08:00:00Z', port: 5123,
}, null, 2);

test('the port and the pid are read from the instance file the registry writes', () => {
  assert.equal(pattern('portMatch').exec(json)?.[1], '5123');
  assert.equal(pattern('pidMatch').exec(json)?.[1], '4321');
});

test('the token is read from the launch file', () => {
  assert.equal(pattern('tokenMatch').exec('http://127.0.0.1:5123/#token=abc-DEF_1\r\n')?.[1], 'abc-DEF_1');
});

test('an instance file without a pid does not match (such a server is never stopped)', () => {
  assert.equal(pattern('pidMatch').exec(json.replace(/"pid": 4321,\s*/, '')), null);
});

test('the patterns carry their backslashes (a pattern like "pid"s*:s*(d+) never matches real JSON)', () => {
  for (const name of ['portMatch', 'pidMatch', 'tokenMatch']) {
    const m = new RegExp(String.raw`Match ${name} = Regex\.Match\([^,]+, @"((?:[^"]|"")*)"`).exec(body);
    const bs = String.fromCharCode(92);
    assert.ok(m[1].includes(bs + 's') || m[1].includes(bs + 'd'), name);
  }
});
