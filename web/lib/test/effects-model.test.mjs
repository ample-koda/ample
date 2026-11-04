import test from 'node:test';
import assert from 'node:assert/strict';
import { EFFECTS, effectModel } from '../effects-model.js';

const defs = [
  { name: 'enabled', default: 'on', kind: 'choice' },
  { name: 'low', default: '0 dB', unit: 'dB', min: -12, max: 12, kind: 'number' },
  { name: 'mid', default: '0 dB', unit: 'dB', min: -12, max: 12, kind: 'number' },
  { name: 'high', default: '0 dB', unit: 'dB', min: -12, max: 12, kind: 'number' },
];

test('effect cards retain parameter names and order and expose their bypass value', () => {
  const model = effectModel('eq', defs, { enabled: 'off', low: '6 dB' });
  assert.equal(model.enabled, 'off');
  assert.deepEqual(model.controls.map((p) => p.name), ['low', 'mid', 'high']);
  assert.deepEqual(model.visual.points, [[0, 18], [50, 24], [100, 24]]);
  assert.deepEqual(EFFECTS, ['eq', 'distortion', 'delay', 'reverb', 'compressor', 'filter']);
});

test('effect visuals use template units and give useful defaults', () => {
  assert.deepEqual(effectModel('compressor', [
    { name: 'threshold', default: '-18 dB', unit: 'dB' }, { name: 'ratio', default: '4' },
  ], {}).visual, { kind: 'compression', threshold: -18, ratio: 4 });
  assert.deepEqual(effectModel('delay', [
    { name: 'time', default: '250 ms', unit: 'ms' }, { name: 'feedback', default: '35%', unit: '%' },
  ], {}).visual, { kind: 'delay', time: 250, feedback: 35 });
  assert.deepEqual(effectModel('reverb', [
    { name: 'size', default: '50%', unit: '%' }, { name: 'decay', default: '60%', unit: '%' },
  ], {}).visual, { kind: 'space', size: 50, decay: 60 });
  assert.equal(effectModel('other', defs, {}), null);
});
