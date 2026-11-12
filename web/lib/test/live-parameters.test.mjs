import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { LIVE_PARAMETERS, parameterLive } from '../live-parameters.mjs';
import { CHAIN_EFFECTS } from '../live-chain-model.mjs';

const templates = new Map(readdirSync(new URL('../../../spec/templates/', import.meta.url)).filter((f) => f.endsWith('.json')).map((f) => { const t = JSON.parse(readFileSync(new URL(`../../../spec/templates/${f}`, import.meta.url), 'utf8')); return [t.id, new Set(t.parameters.flatMap((p) => [p.name, ...(p.aliases || []).map((a) => a.name)]))]; }));

test('every parameter named in the live table is a real parameter of that block', () => {
  for (const [type, entry] of Object.entries(LIVE_PARAMETERS)) {
    const known = templates.get(type);
    assert.ok(known, `${type} is a template`);
    for (const name of [...(entry.param || []), ...(Array.isArray(entry.rebuild) ? entry.rebuild : [])]) assert.ok(known.has(name), `${type}.${name} exists in the template`);
  }
});

test('every block of the live chain is in the table, and unknown blocks need a build', () => {
  for (const type of CHAIN_EFFECTS) assert.ok(LIVE_PARAMETERS[type], `${type} has a live table`);
  assert.equal(parameterLive('reverb', 'mix'), 'param');
  assert.equal(parameterLive('convolution', 'length'), 'rebuild');
  assert.equal(parameterLive('drums', 'pattern'), 'rebuild');
  assert.equal(parameterLive('reverb', 'channels'), 'offline');
  assert.equal(parameterLive('granular', 'grain'), 'offline');
});
