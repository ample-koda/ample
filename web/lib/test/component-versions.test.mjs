import test from 'node:test';
import assert from 'node:assert/strict';
import { pickVersion, templateForVersion } from '../component-versions.mjs';

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

const family = {
  id: 'demo.twice', version: '2.0.0', title: 'Twice',
  inputs: [{ name: 'feed' }], outputs: [{ name: 'notes' }], parameters: [{ name: 'repeats' }], ui: null, cardHeight: 0,
  componentVersions: [
    { version: '2.0.0', view: { inputs: [{ name: 'feed' }], outputs: [{ name: 'notes' }], parameters: [{ name: 'repeats' }], ui: null, cardHeight: 0 } },
    { version: '1.0.0', view: { inputs: [{ name: 'source' }], outputs: [{ name: 'notes' }], parameters: [{ name: 'copies' }], ui: null, cardHeight: 0 } },
  ],
};

test('a block pinned to an older version shows that version ports and properties, side by side with the newer one', () => {
  const older = templateForVersion(family, '1.0.0');
  const newer = templateForVersion(family, '');
  assert.deepEqual(older.inputs.map((p) => p.name), ['source']);
  assert.deepEqual(older.parameters.map((p) => p.name), ['copies']);
  assert.equal(older.version, '1.0.0');
  assert.deepEqual(newer.inputs.map((p) => p.name), ['feed']);
  assert.deepEqual(newer.parameters.map((p) => p.name), ['repeats']);
});

test('a request that matches no version, a template without versions and a version without a view stay as they are', () => {
  assert.equal(templateForVersion(family, '^9.0'), family);
  const plain = { id: 'x', inputs: [] };
  assert.equal(templateForVersion(plain, ''), plain);
  assert.equal(templateForVersion(undefined, '1.0'), undefined);
  const old = { ...family, componentVersions: [{ version: '2.0.0' }] };   // an older server sends no view
  assert.equal(templateForVersion(old, '2.0.0'), old);
});

test('the same version gives the same template object again', () => {
  assert.equal(templateForVersion(family, '1.0.0'), templateForVersion(family, '1'));
});
