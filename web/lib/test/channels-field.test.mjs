import test from 'node:test';
import assert from 'node:assert/strict';
import { channelCount, joinChannels, splitChannels, wholeDb } from '../numfield-model.js';

test('a channels field shows the declared count when the value is shorter or as long', () => {
  assert.equal(channelCount('0 0 0 0', 4), 4);
  assert.equal(channelCount('0 0', 4), 4);
  assert.equal(channelCount('', 4), 4);
  assert.equal(channelCount(undefined, 4), 4);
});

test('a channels field shows every word the value already holds (issue 86: lanes 5 to 16 are not dropped)', () => {
  assert.equal(channelCount('0 -3 -6 -9 -12 -15', 4), 6);
  assert.equal(channelCount('c l r c c r', 4), 6);
  assert.equal(channelCount(Array(16).fill('0').join(' '), 4), 16);
});

test('the declared count falls back to 4 when it is missing', () => {
  assert.equal(channelCount('0 0', undefined), 4);
  assert.equal(channelCount('0 0', 0), 4);
});

test('changing one lane of a six lane value keeps the other five', () => {
  const value = '0 -3 -6 -9 -12 -15';
  const words = splitChannels(value, channelCount(value, 4), '0');
  assert.equal(words.length, 6);
  const written = joinChannels(words.map((w, j) => (j === 0 ? '-1' : w)));
  assert.equal(written, '-1 -3 -6 -9 -12 -15');
});

test('a short value is padded with the fallback up to the declared count', () => {
  const words = splitChannels('-2', channelCount('-2', 4), '0');
  assert.deepEqual(words, ['-2', '0', '0', '0']);
});

test('a typed fraction is written as the whole number the renderer reads (issue 89)', () => {
  assert.equal(wholeDb('-6.5'), '-6');
  assert.equal(wholeDb('-6.6'), '-7');
  assert.equal(wholeDb('2.4'), '2');
  assert.equal(wholeDb('7'), '7');
  assert.equal(wholeDb('-0.4'), '0');
});

test('a value with its unit or spaces is read, and something that is not a number is kept as it is', () => {
  assert.equal(wholeDb(' -3 dB '), '-3');
  assert.equal(wholeDb('-3.2dB'), '-3');
  assert.equal(wholeDb('loud'), 'loud');
  assert.equal(wholeDb(''), '');
  assert.equal(wholeDb(undefined), '');
});
