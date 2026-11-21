import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import { knobStart, numberOf } from '../knob-start.mjs';

const here = (path) => new URL(path, import.meta.url);
const templates = readdirSync(here('../../../spec/templates/')).filter((name) => name.endsWith('.json'))
  .map((name) => JSON.parse(readFileSync(here(`../../../spec/templates/${name}`), 'utf8')));

test('a knob starts from the written value, else the default of the block, else the bottom of its range', () => {
  const definitions = [{ name: 'rate', default: '1' }, { name: 'gain', default: '0 dB' }, { name: 'mix', default: '25%' }];
  assert.equal(knobStart({ rate: '0.8' }, 'rate', definitions, 0.1), 0.8);
  assert.equal(knobStart({}, 'rate', definitions, 0.1), 1);
  assert.equal(knobStart({}, 'gain', definitions, -60), 0);
  assert.equal(knobStart({ gain: '-6 dB' }, 'gain', definitions, -60), -6);
  assert.equal(knobStart({}, 'mix', definitions, 0), 25);
  assert.equal(knobStart({ rate: '' }, 'rate', definitions, 0.1), 1, 'an empty value is not written');
  assert.equal(knobStart({}, 'unknown', definitions, 7), 7);
  assert.equal(knobStart({}, 'rate', undefined, 0.1), 0.1, 'a panel without definitions keeps the old fallback');
  assert.equal(numberOf('50%'), 50);
  assert.equal(numberOf('auto(1:2)'), null);
});

// every knob of a studio panel is made by control(context, params, 'name', 'Label', min, max, ...): the range of the knob must hold the default that
// the spec gives that parameter, else the knob of an unwritten parameter would show a value the block does not use
test('the range of every knob of the studio panels holds the default of its parameter in the spec', () => {
  const dir = here('../../app/studio/panels/');
  const problems = [];
  for (const file of readdirSync(dir).filter((name) => name.endsWith('.js'))) {
    const source = readFileSync(new URL(file, dir), 'utf8');
    for (const match of source.matchAll(/control\(context, \w+, '(\w+)', '([^']+)', (-?[0-9.]+), (-?[0-9.]+)/g)) {
      const [, name, label, min, max] = match;
      const defaults = templates.flatMap((t) => (t.parameters ?? []).filter((p) => p.name === name && numberOf(p.default) !== null).map((p) => numberOf(p.default)));
      if (!defaults.length) continue;
      const fits = defaults.some((value) => value >= Number(min) && value <= Number(max));
      if (!fits) problems.push(`${file}: the knob ${name} (${label}) runs from ${min} to ${max}, the defaults in the spec are ${[...new Set(defaults)].join(', ')}`);
    }
  }
  assert.deepEqual(problems, []);
});
