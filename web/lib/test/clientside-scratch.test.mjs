import test from 'node:test';
import assert from 'node:assert/strict';
import { processScratchBuffer, scratchWorkingSetBytes } from '../clientside-scratch.mjs';

test('untouched scratch steps preserve decoded PCM values', () => {
  const input = Float32Array.of(.25, -.5, .75, -.125), output = processScratchBuffer([input], { pattern: '................' }, 48000, 12000, 1)[0];
  const expected = [...input].map((sample) => Math.round(sample * (sample < 0 ? 32768 : 32767)) / 32768);
  assert.deepEqual([...output], expected);
});

test('forward scratch moves the read point through the source', () => {
  const input = Float32Array.from({ length: 160 }, (_, index) => index / 1000);
  const output = processScratchBuffer([input], { pattern: 'f...............', depth: '10', mix: '100' }, 1000, 160, 1)[0];
  assert.ok(output[5] > input[5]);
});

test('cut steps silence their interior while untouched steps pass through', () => {
  const input = new Float32Array(160).fill(.5), output = processScratchBuffer([input], { pattern: '-...............', mix: '100' }, 1000, 160, 1)[0];
  assert.equal(output[3], 0);
  assert.equal(output[15], input[15]);
});

test('bar selection uses the song bar when previewing a selected range', () => {
  const input = new Float32Array(320).fill(.5), output = processScratchBuffer([input], { pattern: '-...............', bars: '2', mix: '100' }, 1000, 160, 1)[0];
  assert.equal(output[3], input[3]);
  assert.equal(output[163], 0);
});

test('scratch memory estimate grows with frames and channels', () => {
  assert.ok(scratchWorkingSetBytes(2000, 2) > scratchWorkingSetBytes(1000, 2));
  assert.ok(scratchWorkingSetBytes(1000, 2) > scratchWorkingSetBytes(1000, 1));
});
