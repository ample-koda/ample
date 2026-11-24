import assert from 'node:assert/strict';
import { test } from 'node:test';
import { rankFiles, fuzzyScore } from '../fuzzy.mjs';
import { formatValue } from '../studio-math.js';

test('a small negative value with no decimals reads 0, not -0', () => {
  assert.equal(formatValue(-0.4, 0, 'dB'), '0 dB');
  assert.equal(formatValue(-0.001, 2, 'dB'), '0 dB');
  assert.equal(formatValue(-0.001, 2), '0');
  assert.equal(formatValue(-0.5, 0, 'dB'), '-1 dB');   // a value that rounds away from zero keeps its sign
  assert.equal(formatValue(-3.5, 1, 'dB'), '-3.5 dB');
  assert.equal(formatValue(100, 0), '100');
  assert.equal(formatValue(0, 1, 'dB'), '0 dB');
});

test('the file switcher lists only files that match the query, recent or not', () => {
  const paths = ['songs/verse.amplet', 'songs/chorus.amplet', 'samples/kick.wav'];
  assert.deepEqual(rankFiles(paths, 'verse', ['samples/kick.wav']), ['songs/verse.amplet']);   // the recent kick.wav has no v-e-r-s-e: it was listed
  assert.deepEqual(rankFiles(paths, 'chorus', ['songs/chorus.amplet']), ['songs/chorus.amplet']);
});

test('a recent file that matches comes before one that matches as well', () => {
  const paths = ['a/verse-one.amplet', 'b/verse-two.amplet'];
  assert.deepEqual(rankFiles(paths, 'verse', ['b/verse-two.amplet']), ['b/verse-two.amplet', 'a/verse-one.amplet']);
});

test('a match in a very long path is still a match', () => {
  const long = 'a' + '/x'.repeat(200);
  assert.ok(fuzzyScore('a', long) >= 0);
  assert.deepEqual(rankFiles([long, 'b'], 'a'), [long]);
  assert.equal(fuzzyScore('zzz', 'lyrics/chorus.lrc'), -1);
});
