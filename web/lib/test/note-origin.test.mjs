// Step 2 of plan/live-preview/note-origin.md: the origin of every note of a chain, the same rules as the C# evaluator (the shared corpus is run by both).
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate, writeNotes } from '../note-pipeline.mjs';

const corpus = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), '../../../spec-tests/note-origins/origins.json'), 'utf8'));
const originText = (e) => (e.origin ? `${e.origin.source}#${e.origin.index}@${e.origin.lap}` : null);

for (const c of corpus.cases) {
  test(`origins: ${c.id}`, () => {
    const r = evaluate(c.expr, { sources: corpus.sources, tempo: 120 });
    assert.deepEqual(r.errors, []);
    assert.deepEqual(r.value.events.map(originText), c.origins);
  });
}

test('origins: a note an operator makes has none, and the origin is never written', () => {
  const r = evaluate('euclid(3, 8, 60)', { sources: corpus.sources, tempo: 120 });
  assert.deepEqual(r.errors, []);
  assert.ok(r.value.events.every((e) => !e.origin));
  const plain = evaluate('theme.notes |> transpose(+2)', { sources: corpus.sources, tempo: 120 });
  assert.equal(plain.notes, writeNotes(plain.value.events.map(({ origin, ...e }) => e)), 'the written text is the same with or without the origin');
  assert.ok(!plain.notes.includes('theme'));
});

test('origins: originList has the shape of the clip-notes route', async () => {
  const { originList } = await import('../note-pipeline.mjs');
  const r = evaluate('theme.notes |> transpose(+12) |> repeat(2)', { sources: corpus.sources, tempo: 120 });
  const list = originList(r.value.events);
  assert.equal(list.length, 8);
  assert.deepEqual(list[0], { at: 0, len: 1000000, pitch: 72, source: 'theme', index: 0, lap: 0 });
  assert.deepEqual(list[7], { at: 4000000 + 3000000, len: 1000000, pitch: 77, source: 'theme', index: 3, lap: 1 });
  assert.deepEqual(originList(evaluate('euclid(3, 8, 60)', { sources: corpus.sources, tempo: 120 }).value.events)[0].source, null);
});
