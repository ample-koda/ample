// Tokenises sample lines with the generated grammar (vscode-textmate + the Oniguruma regex engine of VS Code)
// and checks the scope of each token class. Needs "npm install" in ample/.
import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(path.join(here, '..', '..', '..', 'package.json'));
let textmate; let oniguruma;
try { textmate = require('vscode-textmate'); oniguruma = require('vscode-oniguruma'); } catch { /* not installed */ }

const grammarPath = path.join(here, '..', 'syntaxes', 'amplet.tmLanguage.json');

async function tokenize(lines) {
  const wasm = fs.readFileSync(require.resolve('vscode-oniguruma/release/onig.wasm')).buffer;
  await oniguruma.loadWASM(wasm);
  const registry = new textmate.Registry({
    onigLib: Promise.resolve({ createOnigScanner: (s) => new oniguruma.OnigScanner(s), createOnigString: (s) => new oniguruma.OnigString(s) }),
    loadGrammar: async () => textmate.parseRawGrammar(fs.readFileSync(grammarPath, 'utf8'), grammarPath),
  });
  const grammar = await registry.loadGrammar('source.amplet');
  let stack = textmate.INITIAL;
  return lines.map((line) => {
    const r = grammar.tokenizeLine(line, stack);
    stack = r.ruleStack;
    return r.tokens.map((t) => ({ text: line.slice(t.startIndex, t.endIndex), scopes: t.scopes }));
  });
}

const has = (tokens, text, scope) => tokens.some((t) => t.text === text && t.scopes.some((s) => s.startsWith(scope)));

test('a song is tokenised into the scopes of each token class', { skip: !textmate && 'vscode-textmate is not installed' }, async () => {
  const lines = [
    '# a comment',
    'composition "demo" { ample 1  tempo 92  master-seed 424242 }',
    'drums beats stable@1.2 at 1 for 16 as "beat A" {',
    '  kit kit/tape',
    '  from chords.notes -> trigger',
    '}',
    'layout beats 120 -40',
  ];
  const t = await tokenize(lines);
  assert.ok(has(t[0], '# a comment', 'comment.line'));
  assert.ok(has(t[1], 'project', 'keyword.control.directive'));
  assert.ok(has(t[1], 'tempo', 'keyword.other'));
  assert.ok(has(t[1], '"demo"', 'string.quoted'));
  assert.ok(has(t[1], '92', 'constant.numeric'));
  assert.ok(has(t[2], 'drums', 'entity.name.type.block'));
  assert.ok(has(t[2], 'beats', 'variable.other.declaration'));
  assert.ok(has(t[2], 'stable', 'storage.type.class'));
  assert.ok(has(t[2], '1.2', 'constant.numeric.version'));
  assert.ok(has(t[2], 'at', 'keyword.control'));
  assert.ok(has(t[2], '"beat A"', 'string.quoted'));
  assert.ok(has(t[3], 'kit', 'entity.other.attribute-name.parameter'));
  assert.ok(has(t[4], 'from', 'keyword.control.from'));
  assert.ok(has(t[4], 'chords', 'variable.other.block'));
  assert.ok(has(t[4], 'notes', 'variable.other.property'));
  assert.ok(has(t[4], '->', 'keyword.operator.arrow'));
  assert.ok(has(t[4], 'trigger', 'variable.parameter'));
  assert.ok(has(t[5], '}', 'punctuation.definition.block.end'));
  assert.ok(has(t[6], 'layout', 'keyword.control.directive'));
  assert.ok(has(t[6], '-40', 'constant.numeric'));
});

test('text outside a block is not mistaken for a parameter', { skip: !textmate && 'vscode-textmate is not installed' }, async () => {
  const t = await tokenize(['  kit kit/tape']);
  assert.ok(!has(t[0], 'kit', 'entity.other.attribute-name.parameter'));
});

test('every block type of the spec is recognised at the start of a line', { skip: !textmate && 'vscode-textmate is not installed' }, async () => {
  const spec = path.join(here, '..', '..', '..', 'spec', 'templates');
  for (const f of fs.readdirSync(spec).filter((x) => x.endsWith('.json'))) {
    const id = JSON.parse(fs.readFileSync(path.join(spec, f), 'utf8')).id;
    const t = await tokenize([`${id} name stable@1.0 {`, '}']);
    assert.ok(has(t[0], id, 'entity.name.type.block'), id);
  }
});

test('the committed grammar and snippets are valid JSON with the expected top-level shape', () => {
  const g = JSON.parse(fs.readFileSync(grammarPath, 'utf8'));
  assert.equal(g.scopeName, 'source.amplet');
  const snippets = JSON.parse(fs.readFileSync(path.join(here, '..', 'snippets', 'amplet.code-snippets'), 'utf8'));
  assert.ok(Object.keys(snippets).length >= 10);
  for (const s of Object.values(snippets)) assert.ok(Array.isArray(s.body) && s.prefix.length === 1);
});
