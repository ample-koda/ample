import test from 'node:test';
import assert from 'node:assert/strict';
import { parseValue, interpolate, takeSnapshot, morphWrites } from '../snapshots.mjs';

test('values with units parse', () => {
  assert.deepEqual(parseValue('-6 dB'), { n: -6, unit: 'dB' });
  assert.deepEqual(parseValue('1500'), { n: 1500, unit: '' });
  assert.equal(parseValue('triangle'), null);
});

test('Hz blends in the log domain: the middle of 100 Hz and 10 kHz is 1 kHz', () => {
  assert.equal(interpolate({ kind: 'number', unit: 'Hz' }, '100 Hz', '10000 Hz', 0.5), '1000 Hz');
  assert.equal(interpolate({ kind: 'number', unit: 'Hz' }, '100 Hz', '10000 Hz', 0), '100 Hz');
  assert.equal(interpolate({ kind: 'number', unit: 'Hz' }, '100 Hz', '10000 Hz', 1), '10000 Hz');
});

test('dB and plain numbers blend linearly and keep their unit', () => {
  assert.equal(interpolate({ kind: 'number', unit: 'dB' }, '-12 dB', '0 dB', 0.5), '-6 dB');
  assert.equal(interpolate({ kind: 'number', unit: '' }, '10', '20', 0.25), '12.5');
});

test('choices step at the middle', () => {
  const choice = { kind: 'choice' };
  assert.equal(interpolate(choice, 'sine', 'square', 0.49), 'sine');
  assert.equal(interpolate(choice, 'sine', 'square', 0.5), 'square');
});

test('a snapshot keeps only the asked names and a morph lists what moves', () => {
  const definitions = [{ name: 'cutoff', kind: 'number', unit: 'Hz', default: '1000 Hz' }, { name: 'type', kind: 'choice', default: 'lowpass' }, { name: 'enabled', kind: 'choice' }];
  const a = takeSnapshot({ cutoff: '200 Hz' }, definitions, ['cutoff', 'type', 'enabled']);
  assert.deepEqual(a, { cutoff: '200 Hz', type: 'lowpass' });
  const b = { cutoff: '5000 Hz', type: 'lowpass' };
  const writes = morphWrites(definitions, a, b, 0.5);
  assert.deepEqual(writes.map((w) => w.name), ['cutoff']);
  assert.equal(writes[0].value, '1000 Hz');
});
