import test from 'node:test';
import assert from 'node:assert/strict';
import { MISSING_FILE_CODE, missingFileDiagnostics } from '../lite-missing-files.mjs';

const NL = String.fromCharCode(10);
const song = (...lines) => lines.join(NL);

test('a file line whose file is not in the project is marked on the quoted name', () => {
  const text = song('composition "t" { ample 1  tempo 120  master-seed 1 }', '', 'import-audio clip at 1 for 1 {', '  file "bestaat.niet.wav"', '}');
  const [d] = missingFileDiagnostics(text, ['song.amplet', 'samples/kick.wav']);
  assert.equal(d.code, MISSING_FILE_CODE);
  assert.equal(d.severity, 'error');
  assert.equal(d.line, 4);
  assert.equal(d.col, 8);   // the opening quote: two spaces, then file, then a space
  assert.equal(d.endCol, 8 + '"bestaat.niet.wav"'.length);
  assert.match(d.message, /bestaat\.niet\.wav.* is not in the project/);
});

test('a file that is there is not marked, with or without ./ in front of it', () => {
  const text = song('import-audio a at 1 for 1 {', '  file "samples/kick.wav"', '}', 'import-audio b at 2 for 1 {', '  file "./samples/kick.wav"', '}');
  assert.deepEqual(missingFileDiagnostics(text, ['samples/kick.wav']), []);
});

test('a pack, an address, a comment and other parameters are left alone', () => {
  const text = song('# file "nothing.wav"', 'drums d at 1 for 1 {', '  kit pack:free/big-rusty-drums/kit', '  file pack:free/x.wav', '  file "https://example.org/a.wav"', '  kit "samples/kit"', '}');
  assert.deepEqual(missingFileDiagnostics(text, []), []);
});

test('a name that differs only in case says so', () => {
  const [d] = missingFileDiagnostics(song('import-audio a at 1 for 1 {', '  file "samples/Kick.WAV"', '}'), ['samples/kick.wav']);
  assert.match(d.message, /Did you mean "samples\/kick\.wav"\?/);
});

test('several missing files are all marked, each on its own line', () => {
  const lines = missingFileDiagnostics(song('  file "a.wav"', '  file "b.wav"', '  file "c.wav"'), ['b.wav']).map((d) => d.line);
  assert.deepEqual(lines, [1, 3]);
  assert.deepEqual(missingFileDiagnostics('', []), []);
  assert.deepEqual(missingFileDiagnostics(undefined, []), []);
});
