import test from 'node:test';
import assert from 'node:assert/strict';
import { compareAudioBuffers, envelopePath } from '../../app/studio/preview/analysis.js';

test('exact preview analysis compares aligned input and output windows', () => {
  const input = Float32Array.from([.5, -.5, .5, -.5, .25, -.25, .25, -.25]);
  const output = Float32Array.from([.25, -.25, .25, -.25, .5, -.5, .5, -.5]);
  const result = compareAudioBuffers(input, output, 2);
  assert.equal(result.input.length, 2);
  assert.ok(result.gainDb[0] < 0);
  assert.ok(result.gainDb[1] > 0);
  assert.equal(compareAudioBuffers(input, output.slice(0, 1), 5).gainDb.length, 1);
  assert.deepEqual(compareAudioBuffers([], [], 128), { input: [], output: [], gainDb: [] });
});

test('waveform envelopes produce scaled SVG path data', () => {
  assert.match(envelopePath([0, .5, 1], 90, 70, 640), /^M0\.00 90\.00 L320\.00 125\.00 L640\.00 20\.00$/);
  assert.equal(envelopePath([], 90, 70, 640), '');
});
