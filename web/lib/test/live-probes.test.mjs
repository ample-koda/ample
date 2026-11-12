import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import test from 'node:test';
import { LIVE_PARAMETERS } from '../live-parameters.mjs';
import { probesFor, valuesFor } from '../live-probes.mjs';

const templates = new Map(readdirSync(new URL('../../../spec/templates/', import.meta.url)).filter((f) => f.endsWith('.json')).map((f) => { const t = JSON.parse(readFileSync(new URL(`../../../spec/templates/${f}`, import.meta.url), 'utf8')); return [t.id, t.parameters]; }));

test('every live parameter has a probe or a written reason why not', () => {
  for (const type of Object.keys(LIVE_PARAMETERS)) {
    const probes = probesFor(type, templates.get(type));
    const entry = LIVE_PARAMETERS[type];
    for (const name of entry.param || []) {
      const probe = probes.find((p) => p.param === name);
      assert.ok(probe, `${type}.${name} has a probe entry`);
      assert.ok(probe.skip ? probe.skip.length > 10 : probe.a !== probe.b, `${type}.${name}: a skip has a reason, a probe has two different values`);
    }
  }
});

test('derived values differ from the default and stay inside the range', () => {
  assert.deepEqual(valuesFor({ default: 'lowpass', options: ['lowpass', 'highpass'] }), { a: 'lowpass', b: 'highpass' });
  const hz = valuesFor({ default: '1000 Hz', min: 20, max: 20000 });
  assert.equal(hz.a, '1000 Hz'); assert.match(hz.b, /Hz$/);
  const v = parseFloat(hz.b); assert.ok(v > 1000 && v <= 20000);
  const db = valuesFor({ default: '0 dB', min: -12, max: 12 });
  assert.equal(db.b, '9.6 dB');
  assert.equal(valuesFor({ default: 'x', min: null, max: null }), null);
});
