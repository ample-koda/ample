import assert from 'node:assert/strict';
import test from 'node:test';
import { chainLevel, chainTyped, controlTargets, controlledValue, curveLevel, fixed, mapLevel } from '../control-live.mjs';

test('fixed reads per mille like the server', () => {
  assert.equal(fixed('0.5', 0), 500); assert.equal(fixed('1', 0), 1000); assert.equal(fixed('-0.25', 0), -250); assert.equal(fixed('x', 7), 7); assert.equal(fixed('"3 Hz"', 0), 3000);
});

test('a curve block holds per mille between its points', () => {
  const params = { value: 'auto(1:0, 5:1000)' };
  assert.equal(curveLevel(params, 1), 0); assert.equal(curveLevel(params, 3), 500); assert.equal(curveLevel(params, 9), 1000);
  assert.equal(curveLevel({ value: 'auto(1:0, 3:10)', unit: 'Hz' }, 3), 10000);   // a typed control holds its unit times 1000
  assert.equal(curveLevel({ value: '0.25' }, 2), 250); assert.equal(curveLevel({ value: 'lfo(1, 2)' }, 2), null);
});

test('map: rescale, invert, limit like the server', () => {
  assert.equal(mapLevel({ out_low: '0', out_high: '4' }, 500), 2000);
  assert.equal(mapLevel({ in_low: '0.2', in_high: '0.8', out_low: '0', out_high: '1' }, 500), 500);
  assert.equal(mapLevel({ invert: 'on' }, 250), 750);
  assert.equal(mapLevel({ out_low: '0', out_high: '1' }, 5000), 1000); assert.equal(mapLevel({ limit: 'off', out_high: '1' }, 5000), 5000);
});

test('a general control is scaled to the range of the parameter, a typed one is the value', () => {
  assert.equal(controlledValue('filter', 'cutoff', 500, false), '10010 Hz');
  assert.equal(controlledValue('delay', 'mix', 250, false), '25 %');
  assert.equal(controlledValue('volume', 'gain', 0, false), '-60 dB');
  assert.equal(controlledValue('filter', 'cutoff', 3200000, true), '3200 Hz');
  assert.equal(controlledValue('eq', 'low', 1, false), null);
});

test('control links from a curve, also through maps, are found for live parameters only', () => {
  const nodes = [{ id: 'sw', tpl: 'curve', params: { value: 'auto(1:0, 5:1000)' } }, { id: 'hz', tpl: 'map', params: { out_high: '4', unit: 'Hz' } }, { id: 'f', tpl: 'filter' }, { id: 'e', tpl: 'eq' }];
  const links = [{ from: 'sw', to: 'hz', type: 'control', inName: 'in' }, { from: 'hz', to: 'f', type: 'control', inName: 'cutoff' }, { from: 'sw', to: 'e', type: 'control', inName: 'low' }];
  const found = controlTargets(nodes, links, () => true);
  assert.deepEqual(found, [{ target: 'f', param: 'cutoff', chain: ['sw', 'hz'], out: 'level' }]);
  assert.deepEqual(controlTargets(nodes, links, () => false), []);
  const byId = new Map(nodes.map((n) => [n.id, n]));
  assert.equal(chainLevel(found[0].chain, byId, 3), 2000);
  assert.equal(chainTyped(found[0].chain, byId), true);
});
