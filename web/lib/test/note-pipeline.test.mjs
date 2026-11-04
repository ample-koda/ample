import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate, parseExpression, writeNotes, readNotes } from '../note-pipeline.mjs';

const dir = path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../spec-tests/note-pipelines');

// the shared corpus: the C# evaluator runs the very same files (tests/Ample.Core.Tests/NotePipelineTests.cs)
for (const file of fs.readdirSync(dir).filter((f) => f.endsWith('.json') && f !== 'seeds.json')) {
  const corpus = JSON.parse(fs.readFileSync(path.join(dir, file), 'utf8'));
  for (const c of corpus.cases) {
    test(`${file}: ${c.id}`, () => {
      const sources = { ...corpus.defaults.sources, ...(c.sources || {}) };
      const r = evaluate(c.expr, { sources, tempo: c.tempo ?? corpus.defaults.tempo, master: BigInt(c.master ?? corpus.defaults.master ?? '0'), streams: c.streams || null, ...(c.limit ? { limit: c.limit } : {}) });
      const expect = c.expect;
      assert.deepEqual(r.errors.map((e) => e.id), expect.errors || [], `errors of ${c.expr}: ${JSON.stringify(r.errors)}`);
      assert.deepEqual(r.warnings.map((w) => w.id), expect.warnings || []);
      if (expect.errors) { assert.equal(r.notes, null); return; }
      assert.equal(r.notes, expect.notes);
      assert.equal(r.span, expect.span);
    });
  }
}

test('an unknown operator names the nearest one', () => {
  const r = evaluate('a.notes |> transpos(1)', { sources: { a: '1:0:1:60:100' } });
  assert.equal(r.errors[0].suggestion, 'transpose');
  assert.deepEqual([r.errors[0].start, r.errors[0].end], [11, 19]);
});

test('the parser reports syntax errors with a position and does not throw', () => {
  const r = parseExpression('a.notes |> (');
  assert.equal(r.ast, null);
  assert.equal(r.errors[0].id, 'syntax');
});

test('routing fields and extensions ride along untouched', () => {
  const r = evaluate('a.notes |> transpose(+2)', { sources: { a: '1:0:1:60:100:0:0:5;bend=50' } });
  assert.equal(r.notes, '1:0:1:62:100:0:0:5;bend=50');
});

test('writeNotes and readNotes agree for plain entries', () => {
  const text = '1:0:1:60:100 1:0.5:0.25:62:90 2:3:1:64:100';
  assert.equal(writeNotes(readNotes(text, [])), text);
});

test('the event cap refuses an absurd result before it is written', () => {
  const r = evaluate('a.notes |> merge(a.notes)', { sources: { a: '1:0:1:60:100 1:1:1:62:100' }, limit: 3 });
  assert.deepEqual(r.errors.map((e) => e.id), ['cap-events']);
});

import { editables, isExpression, stripComments } from '../note-pipeline.mjs';

test('isExpression tells a pipeline from a literal', () => {
  assert.equal(isExpression('take.notes |> bars(1..2)'), true);
  assert.equal(isExpression('(a.notes)'), true);
  assert.equal(isExpression('"1:0:1:60:100"'), false);
  assert.equal(isExpression('1:0:1:60:100 1:1:1:62:100'), false);
  assert.equal(isExpression('__invalid_expanded_notes__'), false);
});

test('comments are removed without moving anything', () => {
  const text = 'a.notes # the source\n  |> transpose(+1) # up';
  assert.equal(stripComments(text).length, text.length);
  const r = evaluate(text, { sources: { a: '1:0:1:60:100' } });
  assert.deepEqual(r.errors, []);
  assert.equal(r.notes, '1:0:1:61:100');
});

test('a bake stage with notes is the start of what comes after it, and the stage before is not run', () => {
  const text = 'melody.notes\n  |> transpose(+3)\n  |> bake low {\n    input-hash "x"\n    notes-hash "y"\n    span 1bars\n    notes 1:0:1:50:100\n  }\n  |> transpose(+1)';
  const r = evaluate(text, { sources: { melody: '1:0:1:60:100' } });
  assert.deepEqual(r.errors, []);
  assert.equal(r.notes, '1:0:1:51:100');
});

test('a bake stage without notes does nothing yet, and one that keeps its notes in a file asks for the server', () => {
  assert.equal(evaluate('a.notes |> bake s |> transpose(+1)', { sources: { a: '1:0:1:60:100' } }).notes, '1:0:1:61:100');
  const filed = evaluate('a.notes |> bake s { file "bakes/x.notes" }', { sources: { a: '1:0:1:60:100' } });
  assert.deepEqual(filed.errors.map((e) => e.id), ['needs-server']);
});

test('a source that only the server can read asks for the server', () => {
  const r = evaluate('song.notes |> transpose(+1)', { sources: { song: { needsServer: true } } });
  assert.deepEqual(r.errors.map((e) => e.id), ['needs-server']);
});

test('clips can read each other, and clips that read each other are refused', () => {
  const chain = evaluate('b.notes |> transpose(+1)', { sources: { a: '1:0:1:60:100', b: 'a.notes |> transpose(+2)' } });
  assert.equal(chain.notes, '1:0:1:63:100');
  const loop = evaluate('a.notes', { sources: { a: 'b.notes', b: 'a.notes' } });
  assert.ok(loop.errors.length > 0);
});

test('editables are listed in the order they stand with their notes', () => {
  const text = 'a.notes\n  |> concat(editable {\n       notes 1:0:1:72:100\n     })\n  |> concat(editable { notes 1:1:1:74:100 })';
  const pieces = editables(text);
  assert.equal(pieces.length, 2);
  assert.equal(pieces[0].entries, '1:0:1:72:100');
  assert.equal(pieces[1].entries, '1:1:1:74:100');
  assert.ok(pieces[0].start < pieces[1].start);
  assert.equal(evaluate(text, { sources: { a: '1:0:1:60:100' } }).notes, '1:0:1:60:100 2:0:1:72:100 3:1:1:74:100');
});

test('named arguments are checked', () => {
  const r = evaluate('a.notes |> humanize(seeed=7)', { sources: { a: '1:0:1:60:100' } });
  assert.deepEqual(r.errors.map((e) => e.id), ['bad-argument']);
  const twice = evaluate('a.notes |> humanize(seed=7, seed=8)', { sources: { a: '1:0:1:60:100' } });
  assert.deepEqual(twice.errors.map((e) => e.id), ['bad-argument']);
});

test('a longer chain of steps gives the same notes as the steps one at a time', () => {
  const sources = { m: '1:0:1:60:100 1:1:1:62:100 2:0:2:67:100' };
  const chain = evaluate('m.notes |> bars(1..3) |> transpose(+2) |> shift(+1beat) |> reverse() |> stretch(2)', { sources });
  const first = evaluate('m.notes |> bars(1..3) |> transpose(+2) |> shift(+1beat)', { sources });
  const step = evaluate('x.notes |> reverse() |> stretch(2)', { sources: { x: first.notes } });
  assert.equal(chain.notes, step.notes);
});

test('a bar is the beats of the song meter during an evaluation, and 4 beats again afterwards (the twin of NotePipeline.UseMeter)', async () => {
  const { BEAT, BAR } = await import('../note-pipeline.mjs');
  const at = (options) => evaluate('1:0:1:60:100 |> shift(1bar)', options).value.events[0].at;
  assert.equal(at({}), 4 * BEAT);
  assert.equal(at({ beatsPerBar: 4 }), 4 * BEAT);
  assert.equal(at({ beatsPerBar: 3 }), 3 * BEAT);
  assert.equal(at({ beatsPerBar: 3.5 }), 3.5 * BEAT);
  assert.equal(at({ beatsPerBar: 5 }), 5 * BEAT);
  assert.equal(BAR, 4 * BEAT);   // the live binding is back to 4/4 after the call
});
