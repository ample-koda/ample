import test from 'node:test';
import assert from 'node:assert/strict';
import { placeImportedAudio } from '../clientside-import-audio.mjs';

const audio = (left, right, sampleRate) => ({ sampleRate, channels: [Float32Array.from(left), ...(right ? [Float32Array.from(right)] : [])] });
const samples = (channel) => [...channel].map((sample) => Math.round(sample * 1e6) / 1e6);

test('imported audio crops the source, applies seconds offset and places it on the song timeline', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { start: '1s', end: '3s', offset: '1s', loop: 'off' }, 4, 2, null, 1, 1);
  assert.deepEqual(samples(result.channels[0]), [0, 0.2, 0.3, 0, 0, 0, 0, 0]);
});

test('imported audio accepts beat offsets and extracts a selected arrangement range', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { offset: '1 beat', loop: 'off' }, 4, 8, { firstBar: 3, bars: 2 }, 3, 1);
  assert.equal(result.channels[0].length, 8);
  assert.deepEqual(samples(result.channels[0]), [0, 0.1, 0.2, 0.3, 0.4, 0, 0, 0]);
});

test('loop crossfade wraps source tails into each repeat', () => {
  const result = placeImportedAudio(audio([0.1, 0.2, 0.3, 0.4], null, 1), { loop: 'on', crossfade: '2s' }, 4, 2, null, 1, 1);
  assert.deepEqual(samples(result.channels[0]), Array(8).fill(0.3));
});

test('stereo channels keep independent source content and align to the output length', () => {
  const result = placeImportedAudio(audio([0.1, 0.2], [0.7, 0.8], 11025), { loop: 'off' }, 4, 1, null, 1, 11025);
  assert.deepEqual(samples(result.channels[0]), [0.1, 0.2, 0, 0]);
  assert.deepEqual(samples(result.channels[1]), [0.7, 0.8, 0, 0]);
});
