import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { resolve, validate, explain } from '../observation-config.mjs';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
const corpus = JSON.parse(fs.readFileSync(path.join(root, 'spec-tests', 'config-layers.json'), 'utf8'));

for (const c of corpus.cases) {
  test(`config layers: ${c.id}`, () => {
    const config = resolve(c.layers);
    for (const [key, [value, layer]] of Object.entries(c.expect)) {
      assert.equal(config.values[key].value, value, `${c.id}: ${key} value`);
      assert.equal(config.values[key].layer, layer, `${c.id}: ${key} layer`);
    }
    assert.equal(config.problems.length, c.problems, c.id + ': problems');
  });
}

test('an effective value can say why it is what it is', () => {
  const config = resolve([{ layer: 'instance', origin: 'up', values: { 'logging.level': ['trace', 12] } }]);
  assert.equal(explain(config, 'logging.level'), 'logging.level is trace: set by the instance up (line 12)');
});

test('values are validated', () => {
  assert.equal(validate('logging.level', 'debug'), null);
  assert.equal(validate('logging.level', 'inherit'), null);
  assert.match(validate('logging.level', 'loud'), /off, warning, info, debug, trace/);
  assert.match(validate('logging.maxTotal', '0'), /at least 1/);
  assert.match(validate('logging.maxTotal', '99999'), /at most 10000/);
  assert.match(validate('logging.nope', '1'), /not a logging or assertion option/);
});
