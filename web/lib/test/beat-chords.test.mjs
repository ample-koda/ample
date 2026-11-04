import test from 'node:test';
import assert from 'node:assert/strict';
import { bassNotes, beatOps, chordsForBars, modeOf, progressionChords } from '../beat-chords.mjs';

test('a progression of roman numerals becomes chords in a key', () => {
  assert.deepEqual(progressionChords('i - VI - III - VII', 'minor', 'A'), ['Am', 'F', 'C', 'G']);
  assert.deepEqual(progressionChords('I - V - vi - IV', 'major', 'C'), ['C', 'G', 'Am', 'F']);
  assert.deepEqual(progressionChords('ii7 - V7 - Imaj7 - vi7 (or Imaj7 - iii7)', 'major', 'C'), ['Dm7', 'G7', 'Cmaj7', 'Am7']);
  assert.deepEqual(progressionChords('i - bVII', 'minor', 'A'), ['Am', 'G']);
  assert.deepEqual(progressionChords('I - bVII - IV', 'major', 'D'), ['D', 'C', 'G']);
  assert.deepEqual(progressionChords('one minor chord', 'minor', 'E'), ['Em']);
  assert.deepEqual(progressionChords('I - IV', 'major', 'Bb'), ['Bb', 'Eb']);
  assert.equal(modeOf('major (jazz colours)'), 'major');
  assert.equal(modeOf('minor'), 'minor');
  assert.deepEqual(chordsForBars(['Am', 'F'], 5), ['Am', 'F', 'Am', 'F', 'Am']);
});

test('the bass line is the pattern on the root of each chord, a hit lasting until the next one', () => {
  assert.equal(bassNotes('x.......x.......', ['A', 'F']), '1:0:2:45:100 1:2:2:45:100 2:0:2:41:100 2:2:2:41:100');
  assert.equal(bassNotes('x...............', ['C']), '1:0:4:36:100');
  assert.equal(bassNotes('', ['C']), '');
});

test('the operations of a groove-set add the drums, the chords with a piano and the bass with its clip, each connected to a target', () => {
  const ops = beatOps({ name: 'house', kit: '', grid: 'kick:x...', fill: '', bar: 1, bars: 4, swing: 0, humanize: 0, chords: ['Am', 'F'], bassPattern: 'x.......x.......', targets: [{ to: 'mix', input: 'in1' }, { to: 'mix', input: 'in2' }, { to: 'mix', input: 'in3' }] });
  const added = ops.filter((o) => o.type === 'addBlock').map((o) => `${o.blockType}:${o.name}`);
  assert.deepEqual(added, ['drums:house', 'midi-clip:house-chords', 'piano:house-keys', 'midi-clip:house-bassline', 'bass:house-bass']);
  assert.equal(ops.find((o) => o.name === 'chords').value, 'Am F Am F');
  assert.deepEqual(ops.filter((o) => o.type === 'connect' && o.to === 'mix').map((o) => [o.from, o.input]), [['house', 'in1'], ['house-keys', 'in2'], ['house-bass', 'in3']]);
  assert.ok(ops.some((o) => o.type === 'connect' && o.from === 'house-chords' && o.to === 'house-keys'));
});
