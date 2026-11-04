import test from 'node:test';
import assert from 'node:assert/strict';
import { blankBlocks, docMarkdown, docsOf, hoverText, markdownBlocks, parseDoc } from '../doc-comments.mjs';
import { NOTE_BUILTINS, NOTE_SIGNATURES } from '../note-catalog.mjs';
import { buildHelperTable, helperSourcesOf } from '../note-lang.mjs';
import { parseDeclaration } from '../note-pipeline.mjs';

const SONG = '/**\n * Moves a voice.\n * @param {Interval|Int} by how far up\n * @param Notes source the notes to copy\n * @return {Notes|Int} the copy\n * @example up(theme.notes)\n */\nhelper up(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\n';

test('blankBlocks keeps the lines and columns, skips strings, # comments and divisions, and reports a comment that is never closed (the twin of Comments.cs)', () => {
  const text = 'a /* one\ntwo */ b\nc "/* not */" d # /* not either\ne 4/*2 f\n';
  const r = blankBlocks(text);
  assert.equal(r.text, 'a       \n       b\nc "/* not */" d # /* not either\ne 4/*2 f\n');
  assert.equal(r.unterminated, -1);
  const open = blankBlocks('x\n  /* open\nstill\n');
  assert.equal(open.unterminated, 1);
  assert.deepEqual(open.ranges, [{ line: 1, start: 2, length: 7 }, { line: 2, start: 0, length: 5 }, { line: 3, start: 0, length: 0 }]);
});

test('a helper declaration that stands under a block comment is found, and a commented-out helper is not', () => {
  const text = SONG + '\n/* helper gone() -> Notes {\n  1:0:1:60:100\n} */\n';
  assert.deepEqual(helperSourcesOf(text).map((s) => s.line), [7]);
});

test('doc comments: summary, typed params (braces or bare, unions), return type, examples and deprecation', () => {
  const docs = docsOf(SONG.split('\n'));
  const doc = docs.get(7);
  assert.equal(doc.summary, 'Moves a voice.');
  assert.deepEqual(doc.params, [{ name: 'by', text: 'how far up', type: 'Interval|Int' }, { name: 'source', text: 'the notes to copy', type: 'Notes' }]);
  assert.equal(doc.returnType, 'Notes|Int');
  assert.equal(doc.returns, 'the copy');
  assert.deepEqual(doc.examples, ['up(theme.notes)']);
  const hashes = docsOf(['## The bass.', '## @deprecated use bass2', 'midi-clip bass at 1 for 1 {', '}', '', '/** orphan */', '', 'x']);
  assert.equal(hashes.get(2).deprecated, 'use bass2');
  assert.equal(hashes.has(7), false);
  assert.equal(parseDoc(['@param source the notes']).params[0].type, null);   // a lowercase first word is the name
});

test('the hover of a helper shows types, the doc comment, the notes and the documented type when it differs; a built-in shows its parameters; units and keywords are explained', () => {
  const table = buildHelperTable(helperSourcesOf(SONG), parseDeclaration);
  const docs = docsOf(SONG.split('\n'));
  const ctx = { helpers: table.signatures(), docs, catalog: NOTE_SIGNATURES, builtins: NOTE_BUILTINS };
  const helper = hoverText('up', '(theme.notes)', ctx);
  assert.match(helper, /up\(Notes source, Interval by\) -> Notes/);
  assert.match(helper, /Moves a voice\./);
  assert.match(helper, /- `Interval by` — how far up \*\(documented as `Interval\|Int`\)\*/);
  assert.match(helper, /- `Notes source` — the notes to copy\n/);
  assert.match(helper, /\*\*Returns\*\* `Notes\|Int` the copy/);
  const shift = hoverText('shift', '(1bar)', ctx);
  assert.match(shift, /shift\(Duration by\) -> Notes/);
  assert.match(shift, /- `Duration by` — How far to move the notes in time/);
  assert.match(shift, /Built in\./);
  assert.match(hoverText('Round', '(2.5)', ctx), /Round\(Number number\) -> Int/);
  assert.match(hoverText('fork', '("a")', { ...ctx, before: 'seed.' }), /Seed\.fork\(Text label\) -> Seed/);
  assert.match(hoverText('bar', ')', { ...ctx, before: 'shift(1' }), /unit/);
  assert.match(hoverText('emit', ' x', { ...ctx, before: '    ' }), /keyword/);
  assert.equal(hoverText('emit', ' x', { ...ctx, before: '    ', insideBlock: true }), null);
  assert.equal(hoverText('nothing', '()', ctx), null);
});

test('every built-in function the evaluator knows has a hover (the catalogue is not missing one)', () => {
  const functions = ['Int', 'Float', 'Round', 'Floor', 'Ceil', 'Truncate', 'count', 'span', 'min', 'max', 'abs', 'at', 'len', 'Note', 'currentTime', 'currentDate', 'osRandom', 'environment'];
  for (const name of functions) assert.ok(NOTE_BUILTINS[name]?.doc, `${name} has an entry with a doc`);
  for (const unit of ['bar', 'bars', 'beat', 'beats', 'tick', 'ticks', 's', 'ms', 'st', 'oct', 'cents', 'dB']) assert.equal(NOTE_BUILTINS[unit]?.kind, 'unit', unit);
  for (const [id, op] of Object.entries(NOTE_SIGNATURES)) for (const p of op.params ?? []) assert.ok(p.doc, `${id}(${p.name}) has a doc`);
});

test('markdownBlocks turns the hover markdown into paragraphs, lists and code', () => {
  const blocks = markdownBlocks('`a(b)` -> x\n\nText with **bold**.\n\n- `T p` — note\n- `U q`\n\n**Example**\n```\nx |> y\n```');
  assert.deepEqual(blocks.map((b) => b.type), ['p', 'p', 'list', 'p', 'code']);
  assert.equal(blocks[2].items.length, 2);
  assert.equal(blocks[4].text, 'x |> y');
  assert.match(docMarkdown(parseDoc(['Hi.', '@example a |> b'])), /\*\*Example\*\*\n```\na \|> b\n```/);
});

test('the doc comment is checked against the declaration (AMP2090 to AMP2094, the twin of DocDiagnostics) and the hover says what is declared', async () => {
  const { docDiagnostics } = await import('../doc-comments.mjs');
  const text = '/**\n * @param {Notes|Int} source the notes\n * @param byy how far\n * @return {Notess} x\n * @deprecated use up2\n */\nhelper up(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\nmidi-clip a at 1 for 1 {\n  notes 1:0:1:60:100 |> up(+2st)\n}\n';
  const lines = text.split('\n');
  const table = buildHelperTable(helperSourcesOf(text), parseDeclaration);
  const found = docDiagnostics(lines, lines, table).map((d) => d.code + '@' + d.range.start.line).sort();
  assert.deepEqual(found, ['AMP2090@6', 'AMP2091@6', 'AMP2091@6', 'AMP2092@6', 'AMP2093@10', 'AMP2094@6']);
  const fine = '/**\n * @param Notes source the notes\n * @param {Interval|Int} by how far\n */\nhelper up(Notes source, Interval by) -> Notes {\n  source |> transpose(by)\n}\n';
  const fineLines = fine.split('\n');
  assert.deepEqual(docDiagnostics(fineLines, fineLines, buildHelperTable(helperSourcesOf(fine), parseDeclaration)).map((d) => d.code), ['AMP2091']);   // only the wider union on "by"
  const alias = 'type Shift = Interval | Int\n\n/**\n * @param Notes source the notes\n * @param {Interval|Int} by how far\n */\nhelper up(Notes source, Shift by) -> Notes {\n  source |> transpose(by)\n}\n';
  const aliasLines = alias.split('\n');
  assert.deepEqual(docDiagnostics(aliasLines, aliasLines, buildHelperTable(helperSourcesOf(alias), parseDeclaration)), []);
  const docs = docsOf(lines);
  const hover = hoverText('up', '(+2st)', { helpers: table.signatures(), docs, catalog: NOTE_SIGNATURES, builtins: NOTE_BUILTINS });
  assert.match(hover, /\*\*Returns\*\* `Notess` x \*\(declared `Notes`\)\*/);
});

test('a union inside the brackets of a generic is one type, and the arguments of a generic are compared as types of their own (the twin of DocTypes.cs)', async () => {
  const { canonicalType, docDiagnostics, splitTop, typeNames } = await import('../doc-comments.mjs');
  assert.deepEqual(splitTop('List<Int|Float>|Notes', '|'), ['List<Int|Float>', 'Notes']);
  assert.deepEqual(typeNames('List<Int|Float>|Notes?'), ['List', 'Int', 'Float', 'Notes']);
  const none = () => null;
  assert.deepEqual([...canonicalType('List<Float|Int>', none)], [...canonicalType('List<Int|Float>', none)]);
  const run = (doc, declared) => {
    const text = 'type Num = Int | Float\n\n/**\n * @param ' + doc + ' xs the list\n */\nhelper f(' + declared + ' xs) -> Notes {\n  return 1:0:1:60:100\n}\n';
    const lines = text.split('\n');
    return docDiagnostics(lines, lines, buildHelperTable(helperSourcesOf(text), parseDeclaration)).map((d) => d.code + ' ' + d.message);
  };
  assert.deepEqual(run('{List<Int>}', 'List<Int>'), []);
  assert.deepEqual(run('{List<Float|Int>}', 'List<Num>'), []);
  assert.deepEqual(run('List<Int | Float>', 'List<Num>'), []);
  const wider = run('{List<Int|Float>}', 'List<Float>');
  assert.equal(wider.length, 1);
  assert.match(wider[0], /^AMP2091/);
  const typo = run('{List<Notess|Int>}', 'List<Int>').filter((m) => m.startsWith('AMP2092'));
  assert.equal(typo.length, 1);
  assert.match(typo[0], /: Notess\.$/);
});
