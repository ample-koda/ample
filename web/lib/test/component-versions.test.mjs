import test from 'node:test';
import assert from 'node:assert/strict';
import { pickVersion } from '../component-versions.mjs';

const versions = ['1.2.0', '1.2.5', '1.9.0', '2.0.0', '0.2.1', '0.2.4', '0.3.0'].map((version) => ({ version }));
const pick = (request, def = '2.0.0') => pickVersion(versions, request, def)?.version ?? null;

test('no request is the default version, a plain request the highest of that line', () => {
  assert.equal(pick(''), '2.0.0');
  assert.equal(pick(undefined, '1.2.0'), '1.2.0');
  assert.equal(pick('1.2'), '1.2.5');
  assert.equal(pick('v1.2'), '1.2.5');
  assert.equal(pick('2'), '2.0.0');
  assert.equal(pick('1.2.0'), '1.2.0');
});

test('a caret is the highest compatible version, as the build takes it (issue 112)', () => {
  assert.equal(pick('^1.2'), '1.9.0');
  assert.equal(pick('^1.2.5'), '1.9.0');
  assert.equal(pick('^2.0'), '2.0.0');
  assert.equal(pick('^0.2'), '0.2.4', 'below 1.0 only the same minor is compatible');
});

test('a tilde stays in the minor version', () => {
  assert.equal(pick('~1.2'), '1.2.5');
  assert.equal(pick('~1.9'), '1.9.0');
  assert.equal(pick('~1.2.5'), '1.2.5');
});

test('nothing that fits gives null, also for a request that is no version', () => {
  assert.equal(pick('^3.0'), null);
  assert.equal(pick('banana'), null);
  assert.equal(pickVersion([], '1.2', '1.2.0'), null);
  assert.equal(pickVersion(undefined, '', ''), null);
});
