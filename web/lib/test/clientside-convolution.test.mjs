import test from 'node:test';
import assert from 'node:assert/strict';
import { convolutionWorkingSetBytes, mapConvolutionImpulse, processConvolutionBuffer } from '../clientside-convolution.mjs';

test('single-sample impulse preserves the input after 16-bit PCM conversion', () => {
  const input = new Float32Array(9000); input[0] = .5; input[4500] = -.25;
  const output = processConvolutionBuffer([input], Float32Array.of(1), { mix: '100', predelay: '0', level: '0' }, 48000)[0];
  assert.ok(Math.abs(output[0] - .5) <= 1 / 32768);
  assert.ok(Math.abs(output[4500] + .25) <= 1 / 32768);
});

test('convolution applies predelay to the wet signal', () => {
  const input = new Float32Array(9000); input[0] = .5;
  const output = processConvolutionBuffer([input], Float32Array.of(1), { mix: '100', predelay: '1', level: '0' }, 1000)[0];
  assert.equal(output[0], 0);
  assert.ok(Math.abs(output[1] - .5) <= 1 / 32768);
});

test('convolution working-set estimate reflects FFT size and channel count', () => {
  assert.ok(convolutionWorkingSetBytes(9000, 2, 2000) > convolutionWorkingSetBytes(9000, 1, 2000));
  assert.ok(convolutionWorkingSetBytes(9000, 1, 10000) > convolutionWorkingSetBytes(9000, 1, 2000));
});

test('impulse mapping averages channels, resamples and normalizes to unit energy', () => {
  const impulse = mapConvolutionImpulse([Float32Array.of(1, 0), Float32Array.of(0, 1)], 2, 4, { length: '3', normalize: 'on' });
  assert.equal(impulse.length, 4);
  assert.ok(Math.abs(impulse.reduce((sum, sample) => sum + sample * sample, 0) - 1) < 1e-6);
});
