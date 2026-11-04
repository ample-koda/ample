// Validates ample/spec/: JSON Schemas, unique ids, grammar equals prose, links, and the conformance corpus structure.
// Dependency-free on purpose (a small JSON Schema subset is implemented here). Run: node ample/tools/validate-spec.mjs
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = process.env.AMPLE_ROOT ?? path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const spec = path.join(root, 'spec'), tests = path.join(root, 'spec-tests'), docs = path.join(root, 'docs', 'language');
const errors = [];
const fail = (where, msg) => errors.push(`${where}: ${msg}`);
const readJson = f => { try { return JSON.parse(fs.readFileSync(f, 'utf8')); } catch (e) { fail(path.relative(root, f), 'invalid JSON: ' + e.message); return null; } };
const walk = d => fs.existsSync(d) ? fs.readdirSync(d, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]) : [];
const rel = f => path.relative(root, f).split(path.sep).join('/');

/** Minimal JSON Schema subset: type, enum, required, properties, additionalProperties, items, pattern. */
function check(value, schema, where) {
  const types = schema.type ? [].concat(schema.type) : null;
  const typeOf = v => v === null ? 'null' : Array.isArray(v) ? 'array' : Number.isInteger(v) ? 'integer' : typeof v;
  if (types && !types.some(t => t === typeOf(value) || (t === 'number' && typeof value === 'number'))) return fail(where, `expected ${types.join('|')}, got ${typeOf(value)}`);
  if (schema.enum && !schema.enum.includes(value)) fail(where, `value ${JSON.stringify(value)} not in ${JSON.stringify(schema.enum)}`);
  if (schema.pattern && typeof value === 'string' && !new RegExp(schema.pattern).test(value)) fail(where, `"${value}" does not match ${schema.pattern}`);
  if (schema.required && value && typeof value === 'object') for (const r of schema.required) if (!(r in value)) fail(where, `missing "${r}"`);
  if (schema.properties && value && typeof value === 'object' && !Array.isArray(value)) {
    for (const [k, v] of Object.entries(value)) {
      if (schema.properties[k]) check(v, schema.properties[k], `${where}.${k}`);
      else if (schema.additionalProperties === false) fail(where, `unknown property "${k}"`);
    }
  }
  if (schema.items && Array.isArray(value)) value.forEach((v, i) => check(v, schema.items, `${where}[${i}]`));
}
const schemaOf = n => readJson(path.join(spec, 'schema', n + '.schema.json'));

// 0. every JSON file in spec/ parses; token patterns compile
for (const f of walk(spec).filter(x => x.endsWith('.json'))) readJson(f);
const tokens = readJson(path.join(spec, 'tokens.json'));
for (const [k, p] of Object.entries(tokens?.patterns ?? {})) { try { new RegExp(p); } catch (e) { fail('spec/tokens.json', `pattern ${k} does not compile: ${e.message}`); } }

// 1. data files against their schemas, unique ids
const version = readJson(path.join(spec, 'version.json')); if (version) check(version, schemaOf('version'), 'spec/version.json');
const diag = readJson(path.join(spec, 'diagnostics.json'));
if (diag) { check(diag, schemaOf('diagnostics'), 'spec/diagnostics.json'); const s = new Set(); for (const d of diag.diagnostics) { if (s.has(d.code)) fail('spec/diagnostics.json', 'duplicate code ' + d.code); s.add(d.code); } }
const liteDiag = readJson(path.join(spec, 'lite-diagnostics.json'));
if (liteDiag) { check(liteDiag, schemaOf('diagnostics'), 'spec/lite-diagnostics.json'); const s = new Set((diag?.diagnostics ?? []).map(d => d.code)); for (const d of liteDiag.diagnostics) { if (!/^AMPL[0-9]{3}$/.test(d.code)) fail('spec/lite-diagnostics.json', d.code + ' is not an AMPL code'); if (s.has(d.code)) fail('spec/lite-diagnostics.json', 'duplicate ' + d.code); s.add(d.code); } }
const lint = readJson(path.join(spec, 'lint-rules.json'));
if (lint) { check(lint, schemaOf('lint-rules'), 'spec/lint-rules.json'); const s = new Set(); for (const r of lint.rules) { if (s.has(r.code)) fail('spec/lint-rules.json', 'duplicate ' + r.code); s.add(r.code); } }
const templates = walk(path.join(spec, 'templates')).filter(f => f.endsWith('.json')).map(f => ({ f, t: readJson(f) })).filter(x => x.t);
const tplSchema = schemaOf('template'); const tplIds = new Set();
for (const { f, t } of templates) { check(t, tplSchema, rel(f)); if (path.basename(f, '.json') !== t.id) fail(rel(f), 'file name must equal id'); if (tplIds.has(t.id)) fail(rel(f), 'duplicate id'); tplIds.add(t.id); }
const types = readJson(path.join(spec, 'types.json')); const typeIds = new Set((types?.types ?? []).map(t => t.id));
for (const { f, t } of templates) for (const p of [...t.inputs, ...t.outputs]) if (!typeIds.has(p.type)) fail(rel(f), `unknown port type ${p.type}`);

// 2. grammar equals the EBNF block in the prose
const ebnfFile = path.join(spec, 'grammar.ebnf'); const syntaxPage = path.join(docs, '03-syntax.md');
if (fs.existsSync(syntaxPage) && fs.existsSync(ebnfFile)) {
  const norm = s => s.replace(/\r/g, '').split('\n').map(l => l.trimEnd()).filter(l => l.length).join('\n');
  const m = /```ebnf\n([\s\S]*?)```/.exec(fs.readFileSync(syntaxPage, 'utf8'));
  if (!m) fail(rel(syntaxPage), 'no ebnf block'); else if (norm(m[1]) !== norm(fs.readFileSync(ebnfFile, 'utf8'))) fail(rel(syntaxPage), 'ebnf block differs from spec/grammar.ebnf');
} else fail('docs/language', '03-syntax.md or spec/grammar.ebnf missing');

// 3. links in docs/language resolve
for (const f of walk(docs).filter(x => x.endsWith('.md'))) {
  for (const m of fs.readFileSync(f, 'utf8').matchAll(/\]\(([^)\s#]+)(#[^)]*)?\)/g)) {
    if (/^(https?:|mailto:)/.test(m[1])) continue;
    if (!fs.existsSync(path.resolve(path.dirname(f), m[1]))) fail(rel(f), 'dead link ' + m[1]);
  }
}

// 4. corpus structure: every .amplet has its expected file; every diagnostic code has an invalid case
const expSchema = schemaOf('expected-diagnostics'); const covered = new Set();
for (const f of walk(path.join(tests, 'invalid')).filter(x => x.endsWith('.amplet'))) {
  const e = f.replace(/\.amplet$/, '.expected-diagnostics.json');
  if (!fs.existsSync(e)) { fail(rel(f), 'missing .expected-diagnostics.json'); continue; }
  const j = readJson(e); if (!j) continue; check(j, expSchema, rel(e));
  if (!j.length) fail(rel(e), 'invalid case must expect at least one diagnostic');
  j.forEach(d => covered.add(d.code));
}
for (const f of walk(path.join(tests, 'valid')).filter(x => x.endsWith('.amplet'))) { const e = f.replace(/\.amplet$/, '.expected.json'); if (!fs.existsSync(e)) fail(rel(f), 'missing .expected.json'); else readJson(e); }
for (const f of walk(path.join(tests, 'format')).filter(x => x.endsWith('.in.amplet'))) if (!fs.existsSync(f.replace(/\.in\.amplet$/, '.out.amplet'))) fail(rel(f), 'missing .out.amplet');
for (const f of walk(path.join(tests, 'recovery')).filter(x => x.endsWith('.amplet'))) if (!fs.existsSync(f.replace(/\.amplet$/, '.expected.json'))) fail(rel(f), 'missing .expected.json');
if (diag) for (const d of diag.diagnostics) if (!d.retired && d.stage !== 'runtime' && !covered.has(d.code)) fail('spec-tests/invalid', `no invalid case produces ${d.code}`);
if (lint) for (const r of lint.rules) if (!covered.has(r.code)) fail('spec-tests/invalid', `no case produces lint rule ${r.code}`);

if (errors.length) { console.error(errors.map(e => '  ' + e).join('\n')); console.error(`validate-spec: ${errors.length} problem(s)`); process.exit(1); }
console.log(`validate-spec: ok (${templates.length} templates, ${diag?.diagnostics.length ?? 0} diagnostics, ${lint?.rules.length ?? 0} lint rules)`);
