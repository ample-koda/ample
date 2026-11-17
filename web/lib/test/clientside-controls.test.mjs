// Which parameters of a block follow a control in the client preview (web/lib/clientside-controls.mjs)
import test from 'node:test';
import assert from 'node:assert/strict';
import { controlledParams } from '../clientside-controls.mjs';

const nodes = [{ id: 'traffic', tpl: 'follow' }, { id: 'shape', tpl: 'curve' }, { id: 'tone', tpl: 'filter' }, { id: 'rec', tpl: 'import-audio' }];

test('a control link to a parameter steers it', () => {
  const tone = { id: 'tone', params: { cutoff: '600 Hz' } };
  assert.deepEqual(controlledParams(tone, nodes, [{ from: 'shape', to: 'tone', inName: 'cutoff', type: 'control' }]), ['cutoff']);
  assert.deepEqual(controlledParams(tone, nodes, [{ from: 'shape', to: 'tone', inName: 'cutoff', type: 'control', disabled: true }]), []);
});

test('an expression that reads a control steers the parameter', () => {
  assert.deepEqual(controlledParams({ id: 'tone', params: { cutoff: '600 + 3000 * traffic.level', resonance: '20 %' } }, nodes, []), ['cutoff']);
});

test('a plain value, an audio link or a name that is not a control steers nothing', () => {
  assert.deepEqual(controlledParams({ id: 'tone', params: { cutoff: '600 Hz', file: 'takes/rec.wav' } }, nodes, [{ from: 'rec', to: 'tone', inName: 'in', type: 'audio' }]), []);
  assert.deepEqual(controlledParams({ id: 'tone', params: { cutoff: 'rec.level' } }, nodes, []), []);
});
