import test from 'node:test';
import assert from 'node:assert/strict';
import { noteProblem, seqDiagnostics } from '../seq-diagnostics.mjs';

const head = 'sequencer 1\nchannel kick drum sample=samples/kick.wav\nchannel bass gm file=pack:musescore-general program=38\n';

test('a note is bar:beat:length:pitch:velocity with the beat and the length in beats', () => {
  assert.equal(noteProblem('1:0:0.75:36:100'), '');
  assert.equal(noteProblem('2:3:1:46:90'), '');
  assert.match(noteProblem('1:6:2:36:90'), /below 4/);
  assert.match(noteProblem('1:3:2:36:90'), /past the end of the bar/);
  assert.match(noteProblem('1:0:1:200:90'), /0 to 127/);
  assert.match(noteProblem('1:0:1:60:0'), /1 to 127/);
  assert.match(noteProblem('1:0:1:60'), /five numbers/);
});

test('the notes of a row are checked with the line and the columns of the token', () => {
  const text = `${head}pattern groove bars=1\n  bass 1:0:1:36:100 1:6:2:36:90\n`;
  const found = seqDiagnostics(text, ['samples/kick.wav']);
  assert.equal(found.length, 1);
  assert.equal(found[0].range.start.line, 4);
  assert.equal(text.split('\n')[4].slice(found[0].range.start.character, found[0].range.end.character), '1:6:2:36:90');
  assert.match(found[0].message, /bass/);
});

test('a file that is not in the project is a problem, a pack and a folder of a set are not', () => {
  const text = `sequencer 1\nchannel kick drum sample=samples/kick.wav\nchannel gtr guitar set=samples/guitar\nchannel b gm file=pack:musescore-general program=1\n`;
  assert.equal(seqDiagnostics(text, ['samples/guitar/a2.wav']).filter((d) => /kick/.test(d.message)).length, 1);
  assert.equal(seqDiagnostics(text, ['samples/kick.wav', 'samples/guitar/a2.wav']).length, 0);
  assert.equal(seqDiagnostics(text, null).length, 0);   // without a list of files the samples are not checked
});

test('a mistake of the format itself is reported on its line, and a good file has no problems', () => {
  const broken = 'sequencer 1\nchannel kick drum\npattern p bars=1\n  kick x...\nplaylist\n  banana\n';
  const found = seqDiagnostics(broken, null);
  assert.ok(found.some((d) => d.range.start.line === 5));
  assert.equal(seqDiagnostics(`${head}pattern g bars=1\n  kick ....\n`, ['samples/kick.wav']).length, 0);
});
