import test from 'node:test';
import assert from 'node:assert/strict';
import { missingFileDiagnostics, similarFile, toLspDiagnostic } from '../file-refs.mjs';

const PATHS = ['song.amplet', 'samples/Kick.wav', 'samples/snare.wav', 'samples/hat open.wav', 'fonts/piano.sf2'];

test('a name that is near exactly one file of the project suggests that file', () => {
  assert.equal(similarFile('samples/kick.wav', PATHS), 'samples/Kick.wav');   // other letters
  assert.equal(similarFile('kick.wav', PATHS), 'samples/Kick.wav');   // another folder
  assert.equal(similarFile('samples/hat-open.wav', PATHS), 'samples/hat open.wav');   // a space or a dash
  assert.equal(similarFile('samples/snar.wav', PATHS), 'samples/snare.wav');   // a letter away
  assert.equal(similarFile('samples/Kick.flac', PATHS), 'samples/Kick.wav');   // another extension
});

test('no suggestion when nothing is near, when several are, or when the name is too short to guess from', () => {
  assert.equal(similarFile('samples/bestaat.niet.wav', PATHS), null);
  assert.equal(similarFile('kick.wav', [...PATHS, 'drums/KICK.wav']), null, 'two files with that name: a guess between them is none');
  assert.equal(similarFile('a.wav', ['b.wav']), null);
  assert.equal(similarFile('song.amplet', PATHS), null, 'a song is never suggested');
});

test('a missing file says which file it probably means and carries the fix, and nothing when there is none', () => {
  const NL = String.fromCharCode(10);
  const [near, far] = missingFileDiagnostics(['  file "samples/kick.wav"', '  file "samples/bestaat.niet.wav"'].join(NL), PATHS);
  assert.match(near.message, /Did you mean "samples\/Kick\.wav"\?/);
  assert.match(near.message, /case sensitive/);
  assert.deepEqual(near.fix, { label: 'Use "samples/Kick.wav" instead', text: '"samples/Kick.wav"' });
  assert.equal(far.fix, undefined);
  assert.match(far.message, /Upload it, or change the name/);
});

test('the diagnostic of the language server has 0-based places and keeps the fix', () => {
  const [d] = missingFileDiagnostics('  file "kick.wav"', PATHS);
  const lsp = toLspDiagnostic(d);
  assert.deepEqual(lsp.range, { start: { line: 0, character: 7 }, end: { line: 0, character: 17 } });
  assert.equal(lsp.severity, 1);
  assert.equal(lsp.code, 'AMPL925');
  assert.equal(lsp.data.fix.text, '"samples/Kick.wav"');
});
