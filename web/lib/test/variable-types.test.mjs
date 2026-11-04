import test from 'node:test';
import assert from 'node:assert/strict';
import { docsOf } from '../doc-comments.mjs';
import { buildHelperTable, helperSourcesOf } from '../note-lang.mjs';
import { parseDeclaration } from '../note-pipeline.mjs';
import { variableHover, variableSpots } from '../variable-types.mjs';

const SONG = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\nlet base = 60\nlet gap = 2bars\n\n/**\n * Builds a phrase.\n * @param count how many bars\n */\nhelper phrase(Int count, Interval up = +2st) -> Notes {\n  let motif = 1:0:0.5:76:86 1:1:0.25:79:78\n  var amount = 1\n  amount = 2.5\n  let name = "verse"\n  let flags = [1, 2.5]\n  let total = count * 2 / 4\n  let moved = motif |> transpose(up)\n  for step in 0..count {\n    emit moved |> shift(step * gap)\n  }\n  return moved\n}\n';
const lines = SONG.split('\n');
const table = buildHelperTable(helperSourcesOf(SONG), parseDeclaration);
const spots = variableSpots(table);
const docs = docsOf(lines);
const at = (needle, occurrence = 0) => { let seen = 0; for (let i = 0; i < lines.length; i++) for (let from = 0, k = lines[i].indexOf(needle, from); k >= 0; from = k + 1, k = lines[i].indexOf(needle, from)) if (seen++ === occurrence) return [i, k + 1]; throw new Error(needle); };
const hover = (needle, occurrence = 0, word = needle) => { const [l, c] = at(needle, occurrence); return variableHover(spots, lines, l, c, word, docs) ?? '(nothing)'; };

test('a let or var shows its value, the type that was worked out and the types it can have (the twin of the C# hover)', () => {
  const motif = hover('motif', 0);
  assert.match(motif, /`let motif: Notes`/);
  assert.match(motif, /= `1:0:0.5:76:86 1:1:0.25:79:78`/);
  assert.match(motif, /\*\*Type\*\* `Notes` — from a list of notes/);
  const amount = hover('amount', 0);
  assert.match(amount, /`var amount: Int \| Float`/);
  assert.match(amount, /\*\*Potential types\*\* `Int`, `Float` — it is assigned values of these types\./);
  assert.match(hover('flags', 0), /`let flags: List<Int \| Float>`/);
  assert.match(hover('name', 0), /`let name: String`/);
  assert.match(hover('total', 0), /`let total: Float`/);
  assert.match(hover('moved', 0), /the call of transpose\(\)/);
});

test('uses of a variable, a parameter, a loop variable and a constant show their types, the let keyword says what it declares', () => {
  assert.match(hover('moved', 1), /`let moved: Notes`/);
  const count = hover('count', 1);
  assert.match(count, /`parameter count: Int`/);
  assert.match(count, /how many bars/);
  assert.match(hover('up', 1), /`parameter up: Interval`/);
  assert.match(hover('step', 1), /`loop variable step: Int`/);
  const gap = hover('gap', 1);
  assert.match(gap, /`let gap: Length`/);
  assert.match(gap, /constant/);
  assert.match(hover('base', 0), /`let base: Int`/);
  const keyword = hover('let motif', 0, 'let');
  assert.match(keyword, /`let` — keyword/);
  assert.match(keyword, /Declares `motif: Notes`\./);
});

test('a helper that does not say what it returns gets the type of what its body returns (the twin of the C# hover)', async () => {
  const { hoverText } = await import('../doc-comments.mjs');
  const { NOTE_BUILTINS, NOTE_SIGNATURES } = await import('../note-catalog.mjs');
  const text = 'composition "x" { ample 1  tempo 100  master-seed 1 }\n\nhelper pulse(Notes n) {\n  emit n |> shift(1bar)\n}\n\nhelper size() {\n  let x = 1\n  return x\n}\n\nhelper pick(Int n) {\n  if n > 3 {\n    return 1\n  } else {\n    return 2.5\n  }\n}\n\nhelper double(Int n) {\n  n * 2\n}\n\nhelper user(Notes n) -> Notes {\n  let count = size()\n  let chosen = pick(2)\n  let notes = pulse(n)\n  return notes\n}\n';
  const l = text.split('\n');
  const t = buildHelperTable(helperSourcesOf(text), parseDeclaration);
  const s = variableSpots(t);
  const ctx = { helpers: t.signatures(), docs: new Map(), catalog: NOTE_SIGNATURES, builtins: NOTE_BUILTINS, inferred: s.returns };
  assert.match(hoverText('pulse', '(n)', ctx), /pulse\(Notes n\) -> Notes/);
  assert.match(hoverText('pulse', '(n)', ctx), /Returns `Notes` — worked out from what the body returns\./);
  assert.match(hoverText('size', '()', ctx), /size\(\) -> Int/);
  assert.match(hoverText('pick', '(2)', ctx), /pick\(Int n\) -> Int \| Float/);
  assert.match(hoverText('double', '(2)', ctx), /double\(Int n\) -> Int/);
  const at = (needle) => { const i = l.findIndex((x) => x.includes('let ' + needle)); return [i, l[i].indexOf(needle) + 1]; };
  const hover = (needle) => { const [i, c] = at(needle); return variableHover(s, l, i, c, needle) ?? '(nothing)'; };
  assert.match(hover('count'), /`let count: Int`/);
  assert.match(hover('chosen'), /`let chosen: Int \| Float`/);
  assert.match(hover('notes'), /`let notes: Notes`/);
});
