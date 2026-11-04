import assert from 'node:assert/strict';
import test from 'node:test';
import { findClips, tempoOf, withNotes } from '../lite-roll-source.mjs';

const SONG = [
  'composition "x" { ample 1  tempo 96  master-seed 1 }',
  '',
  'midi-clip riff at 1 for 4 {',
  '  bars 4',
  '  notes 1:0:1:60:100 1:1:1:64:100',
  '}',
  '',
  'midi-clip chordy at 1 for 4 {',
  '  chords Am F C G',
  '  bars 4',
  '}',
  '',
  'midi-clip expanded at 1 for 1 {',
  '  notes {',
  '    note { bar 1 }',
  '  }',
  '}',
  '',
].join('\n');

test('the clips of a song, with the value of the notes line', () => {
  const clips = findClips(SONG);
  assert.deepEqual(clips.map((c) => c.name), ['riff', 'chordy', 'expanded']);
  const riff = clips[0];
  assert.equal(riff.rollable, true);
  assert.equal(riff.value, '1:0:1:60:100 1:1:1:64:100');
  assert.equal(SONG.slice(riff.from, riff.to), riff.value);
  assert.equal(riff.bars, 4);
  assert.equal(clips[1].rollable, false);
  assert.match(clips[1].why, /chords or a melody/);
  assert.equal(clips[2].rollable, false);
});

test('replacing the value changes that line only', () => {
  const [riff] = findClips(SONG);
  const next = withNotes(SONG, riff, '1:0:1:62:90');
  assert.equal(next, SONG.replace('1:0:1:60:100 1:1:1:64:100', '1:0:1:62:90'));
});

test('the tempo comes from the project line', () => {
  assert.equal(tempoOf(SONG), 96);
  assert.equal(tempoOf('midi-clip a {\n}'), 120);
});
