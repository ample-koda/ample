import test from 'node:test';
import assert from 'node:assert/strict';
import { cutBefore, applyOffset, encodeWav, findOnset, joinChunks, levelOf, normalize, peaksOf, takeFileName, toInt16, trim } from '../recorder-model.mjs';

test('the WAV encoder writes a mono 16-bit PCM file that reads back', () => {
  const samples = Float32Array.from([0, 0.5, -0.5, 1, -1, 2]);
  const bytes = encodeWav(samples, 44100);
  const view = new DataView(bytes.buffer);
  assert.equal(String.fromCharCode(...bytes.slice(0, 4)), 'RIFF');
  assert.equal(String.fromCharCode(...bytes.slice(8, 12)), 'WAVE');
  assert.equal(view.getUint16(20, true), 1);
  assert.equal(view.getUint16(22, true), 1);
  assert.equal(view.getUint32(24, true), 44100);
  assert.equal(view.getUint16(34, true), 16);
  assert.equal(view.getUint32(40, true), 12);
  assert.equal(bytes.length, 44 + 12);
  assert.deepEqual([...toInt16(samples)], [0, 16384, -16384, 32767, -32768, 32767]);
  assert.equal(view.getInt16(44 + 2, true), 16384);
});

test('trim keeps the chosen fractions and never returns nothing', () => {
  const s = Float32Array.from({ length: 100 }, (_, i) => i);
  assert.equal(trim(s, 0.25, 0.75).length, 50);
  assert.equal(trim(s, 0.25, 0.75)[0], 25);
  assert.equal(trim(s, 0.9, 0.1).length, 1);
});

test('a latency offset drops the first samples, a negative one pads silence', () => {
  const s = Float32Array.from([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
  assert.deepEqual([...applyOffset(s, 3, 1000)], [4, 5, 6, 7, 8, 9, 10]);
  assert.deepEqual([...applyOffset(s, -2, 1000)].slice(0, 4), [0, 0, 1, 2]);
  assert.equal(applyOffset(s, 0, 1000), s);
});

test('normalise reaches the peak and leaves silence alone', () => {
  const out = normalize(Float32Array.from([0.1, -0.2, 0.05]), 0.9);
  assert.ok(Math.abs(Math.max(...out.map(Math.abs)) - 0.9) < 1e-6);
  const silent = Float32Array.from([0, 0]);
  assert.equal(normalize(silent), silent);
});

test('peaks, chunks, level and file names', () => {
  const p = peaksOf(Float32Array.from([0, 1, -1, 0.5]), 2);
  assert.deepEqual([...p], [0, 1, -1, 0.5]);
  assert.deepEqual([...joinChunks([Float32Array.from([1, 2]), Float32Array.from([3])])], [1, 2, 3]);
  assert.equal(levelOf(Float32Array.from([0.2, -0.7])), Math.fround(0.7));
  assert.equal(takeFileName(3), 'recording-03.wav');
});

test('the onset of a click is found and silence is not a click', () => {
  const s = new Float32Array(1000);
  s[300] = 0.3; s[301] = 0.8; s[302] = 0.3;
  assert.equal(findOnset(s), 301);
  assert.equal(findOnset(new Float32Array(100)), -1);
  assert.equal(findOnset(Float32Array.from([0.001, -0.002])), -1);
});

test('cutBefore drops what was recorded before the downbeat, counted in frames of the audio clock', () => {
  const samples = Float32Array.from([1, 2, 3, 4, 5, 6]);
  assert.deepEqual([...cutBefore(samples, 1000, 1002)], [3, 4, 5, 6]);
  assert.deepEqual([...cutBefore(samples, 1000, 1000)], [1, 2, 3, 4, 5, 6]);
  assert.deepEqual([...cutBefore(samples, 1000, 900)], [1, 2, 3, 4, 5, 6]);     // a downbeat before the first sample cuts nothing
  assert.deepEqual([...cutBefore(samples, 1000, 5000)], []);                    // one after the last sample leaves nothing
});
