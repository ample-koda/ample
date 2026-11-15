import test from 'node:test';
import assert from 'node:assert/strict';
import { fileChoices, fileValueContext } from '../lite-missing-files.mjs';
import { diagnosticsAt, tipClass, tipText } from '../lite-diagnostic-tip.mjs';

test('the caret in the quotes of a file line is a file value, with what is typed of it', () => {
  assert.deepEqual(fileValueContext('  file "sam', '"'), { from: 8, prefix: 'sam', open: true, closed: true });
  assert.deepEqual(fileValueContext('  file "', ''), { from: 8, prefix: '', open: true, closed: false });
  assert.deepEqual(fileValueContext('file ', ''), { from: 5, prefix: '', open: false, closed: false });
  assert.deepEqual(fileValueContext('  file kic', ''), { from: 7, prefix: 'kic', open: false, closed: false });
});

test('other lines, a finished value and a comment are not a file value', () => {
  assert.equal(fileValueContext('  kit "sam', ''), null);
  assert.equal(fileValueContext('  filename "x', ''), null);
  assert.equal(fileValueContext('  file "a.wav" ', ''), null);
  assert.equal(fileValueContext('  file # a', ''), null);
  assert.equal(fileValueContext('# file "a', ''), null);
});

test('the files of the project are offered: sound first, never a song, matched anywhere in the path', () => {
  const paths = ['song.amplet', 'notes.txt', 'samples/Kick.wav', 'samples/snare.flac', 'fonts/piano.sf2', 'samples/readme.md'];
  assert.deepEqual(fileChoices(paths, '').map((c) => c.label), ['fonts/piano.sf2', 'samples/Kick.wav', 'samples/snare.flac', 'notes.txt', 'samples/readme.md']);
  assert.deepEqual(fileChoices(paths, 'KICK').map((c) => c.label), ['samples/Kick.wav']);
  assert.deepEqual(fileChoices(paths, 'samples/').map((c) => c.detail), ['sound file', 'sound file', 'file']);
  assert.deepEqual(fileChoices(paths, 'zzz'), []);
});

test('the diagnostics that cover a place are found, a mark being at least one character wide', () => {
  const list = [{ line: 4, col: 8, endCol: 26, severity: 'error', code: 'AMPL925', message: 'x' }, { line: 4, col: 30, severity: 'warning', message: 'y' }];
  assert.equal(diagnosticsAt(list, { line: 3, character: 7 }).length, 1);
  assert.equal(diagnosticsAt(list, { line: 3, character: 25 }).length, 0);   // endCol 26 is not included
  assert.equal(diagnosticsAt(list, { line: 3, character: 29 })[0].severity, 'warning');
  assert.equal(diagnosticsAt(list, { line: 2, character: 7 }).length, 0);
  assert.deepEqual(diagnosticsAt(undefined, { line: 0, character: 0 }), []);
});

test('the tip says what is wrong first and then the help of the language server, in a colour', () => {
  const hit = { severity: 'error', code: 'AMPL925', message: 'The file is not in the project.' };
  assert.equal(tipText([hit], '`file` is the path of a sound'), 'Error AMPL925: The file is not in the project.' + String.fromCharCode(10, 10) + 'file is the path of a sound');
  assert.equal(tipText([], '**help**'), 'help');
  assert.equal(tipText([{ severity: 'warning', message: 'w' }], null), 'Warning: w');
  assert.equal(tipClass([hit]), 'lite-tip lite-tip-error');
  assert.equal(tipClass([{ severity: 'warning' }]), 'lite-tip lite-tip-warning');
  assert.equal(tipClass([]), 'lite-tip');
});
