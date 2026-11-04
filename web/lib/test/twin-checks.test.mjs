import test from 'node:test';
import assert from 'node:assert/strict';
import { buildHelperTable, helperDiagnostics, helperSourcesOf } from '../note-lang.mjs';
import { evaluate, parseDeclaration } from '../note-pipeline.mjs';
import { lengthDiagnostics } from '../block-length.mjs';

const table = (text) => buildHelperTable(helperSourcesOf(text), parseDeclaration);
const HELPER = 'helper up(Notes source) -> Notes {\n  source |> transpose(+12)\n}\n';
const BLOCK = 'midi-clip theme at 1 for 4 {\n  notes 1:0:1:60:100\n}\n';

test('a helper that nothing in the song calls is an info (AMP2086), a helper that is called is not, and a library of helpers is left alone', () => {
  const unused = `composition "x" { ample 1  tempo 100 }\n\n${HELPER}\n${BLOCK}`;
  const found = helperDiagnostics(unused, table(unused)).filter((d) => d.code === 'AMP2086');
  assert.equal(found.length, 1);
  assert.equal(found[0].severity, 3);
  assert.match(found[0].message, /"up" is never called in this song/);
  const used = `composition "x" { ample 1  tempo 100 }\n\n${HELPER}\nmidi-clip theme at 1 for 4 {\n  notes 1:0:1:60:100 |> up()\n}\n`;
  assert.equal(helperDiagnostics(used, table(used)).filter((d) => d.code === 'AMP2086').length, 0);
  const library = HELPER;
  assert.equal(helperDiagnostics(library, table(library)).filter((d) => d.code === 'AMP2086').length, 0);
  const commented = `composition "x" { ample 1  tempo 100 }\n\n${HELPER}\n# up is not used yet\n${BLOCK}`;
  assert.equal(helperDiagnostics(commented, table(commented)).filter((d) => d.code === 'AMP2086').length, 1);   // a comment is not a call
});

test('the call of a helper whose declaration has an error says so (AMP2087) instead of an unknown operator', () => {
  const text = 'helper broken(Notes source -> Notes {\n  source\n}\n';
  const r = evaluate('1:0:1:60:100 |> broken()', { tempo: 100, helpers: table(text) });
  assert.equal(r.errors[0]?.id, 'helper-broken');
  assert.match(r.errors[0].message, /declaration has an error/);
  const other = evaluate('1:0:1:60:100 |> nothingLikeIt()', { tempo: 100, helpers: table(text) });
  assert.equal(other.errors[0]?.id, 'unknown-operator');
});

test('a block without a length that is fed by a longer MIDI file is warned about (AMP2089), and not when it has a length, bars or a short file', () => {
  const lines = ['composition "x" {}', '', 'midi score {', '  bars 15', '}', '', 'midi-render sound {', '  from score.sequence -> sequence', '}'];
  const blocks = [
    { name: 'score', type: 'midi', class: 'stable', version: '1.0', line: 2, parts: [], parameters: [{ name: 'bars', value: '15' }], froms: [] },
    { name: 'sound', type: 'midi-render', class: 'stable', version: '1.0', line: 6, parts: [], parameters: [], froms: [{ block: 'score', output: 'sequence', input: 'sequence', disabled: false }] },
  ];
  const found = lengthDiagnostics(blocks, lines);
  assert.equal(found.length, 1);
  assert.equal(found[0].code, 'AMP2089');
  assert.equal(found[0].range.start.line, 6);
  assert.match(found[0].message, /15 bars long/);
  assert.equal(lengthDiagnostics([blocks[0], { ...blocks[1], parts: [{ at: 1, length: 15 }] }], lines).length, 0);
  assert.equal(lengthDiagnostics([blocks[0], { ...blocks[1], parameters: [{ name: 'bars', value: '15' }] }], lines).length, 0);
  assert.equal(lengthDiagnostics([{ ...blocks[0], parameters: [{ name: 'bars', value: '6' }] }, blocks[1]], lines).length, 0);
});

test('a midi-clip that feeds a block counts too when the caller can work out its notes', () => {
  const lines = ['clip', '', 'sound'];
  const blocks = [
    { name: 'tune', type: 'midi-clip', class: 'stable', version: '1.1', line: 0, parts: [{ at: 1, length: 12 }], parameters: [], froms: [] },
    { name: 'sound', type: 'gm', class: 'stable', version: '1.0', line: 2, parts: [], parameters: [], froms: [{ block: 'tune', output: 'notes', input: 'notes', disabled: false }] },
  ];
  assert.equal(lengthDiagnostics(blocks, lines).length, 0);   // without a way to know the notes nothing is said
  assert.equal(lengthDiagnostics(blocks, lines, { clipBars: () => 12 }).length, 1);
  assert.equal(lengthDiagnostics(blocks, lines, { clipBars: () => 6 }).length, 0);
});
