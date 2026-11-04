import test from 'node:test';
import assert from 'node:assert/strict';
import { granularWorkingSetBytes, parseEffectiveGranularSeed, processGranularBuffer } from '../clientside-granular.mjs';

const source = Float32Array.from({ length: 48000 }, (_, index) => Math.sin(index * .071) * .2);
const params = { grain: '80', density: '35', scatter: '55', pitch: '3' };

test('granular preview is deterministic for the same full-width AMPLE seed', () => {
  const a = processGranularBuffer([source], 48000, params, '18446744073709551000')[0];
  const b = processGranularBuffer([source], 48000, params, '18446744073709551000')[0];
  assert.deepEqual(a, b);
});

test('effective seed extraction keeps AMPLE uint64 values exact', () => {
  assert.equal(parseEffectiveGranularSeed('{"seed":{"value":18446744073709551615,"locked":true}}'), '18446744073709551615');
  assert.equal(parseEffectiveGranularSeed('{"seed":{"value":"18446744073709551615","locked":true}}'), '18446744073709551615');
  assert.equal(parseEffectiveGranularSeed('{"seed":null}'), null);
});

test('granular seed zero uses the same fixed fallback as the server', () => {
  assert.deepEqual(processGranularBuffer([source], 48000, params, '0')[0], processGranularBuffer([source], 48000, params, '11400714819323198485')[0]);
});

test('granular preview responds to seed changes and uses one shared stereo schedule', () => {
  const left = processGranularBuffer([source], 48000, params, '12345')[0];
  const changed = processGranularBuffer([source], 48000, params, '12346')[0];
  const stereo = processGranularBuffer([source, source], 48000, params, '12345');
  assert.notDeepEqual(left, changed);
  assert.deepEqual(stereo[0], stereo[1]);
});

test('granular memory estimate includes input copies and float/double output buffers', () => {
  assert.equal(granularWorkingSetBytes([new Float32Array(100)]), 1600);
});
