// The language layer for the browser (docs/language/14-helpers-types-and-dynamic.md): helpers in any order, typed parameters, let/var, if / else if / else, bounded loops, recursion with a stack-depth guard,
// fuel, dynamic helpers and the clock, `bake EXPR as VALUE`. The twin of src/Ample.Language/NoteLang*.cs: both run spec-tests/note-lang/core.json and must give the same notes. The operators themselves are
// the ones of note-pipeline.mjs (this layer evaluates arguments and control flow and hands every operator to it). No recipes or hashes here: the server is authoritative for those. @ample:note-lang

import { blankBlocks } from './doc-comments.mjs';
import { NOTE_SIGNATURES, NOTE_TYPES } from './note-catalog.mjs';
import { noteNameToPitch, typedValue } from './note-typed.mjs';
import { tryChord, tryRoot } from './note-random.mjs';
import { sha256 } from './sha256.mjs';

export const DEFAULT_LIMITS = { loopIterations: 10000, whileDefault: 256, stackDepth: 64, fuel: 2000000, memoryMb: 64 };
export const LIMIT_CAPS = { loopIterations: 1000000, whileDefault: 10000, stackDepth: 256, fuel: 100000000, memoryMb: 1024 };
const VOLATILE = ['currenttime', 'currentdate', 'osrandom', 'environment'];
const LANG_FUNCTIONS = ['Error', 'RequirementError', 'Int', 'Float', 'Round', 'Floor', 'Ceil', 'Truncate', 'count', 'span', 'min', 'max', 'abs', 'at', 'String', 'sprintf',
    'len', 'Note', 'emptyNotes', 'currentTime', 'currentDate', 'osRandom', 'environment', 'random', 'choose', 'chance', 'stable', 'random-seed'];

/** The limits of a song from its settings (limits.loop-iterations ...), clamped to the caps of the server. */
export function limitsFrom(settings = {}) {
  const read = (key, fallback, cap) => { const raw = settings['limits.' + key]; const n = raw === undefined ? NaN : Number(String(raw).replace(/"/g, '')); return Math.min(cap, Math.max(1, Number.isFinite(n) ? n : fallback)); };
  return { loopIterations: read('loop-iterations', DEFAULT_LIMITS.loopIterations, LIMIT_CAPS.loopIterations), whileDefault: read('while-default', DEFAULT_LIMITS.whileDefault, LIMIT_CAPS.whileDefault), stackDepth: read('stack-depth', DEFAULT_LIMITS.stackDepth, LIMIT_CAPS.stackDepth), fuel: read('fuel', DEFAULT_LIMITS.fuel, LIMIT_CAPS.fuel), memoryMb: read('memory', DEFAULT_LIMITS.memoryMb, LIMIT_CAPS.memoryMb) };
}

// ---------- the helpers of a file

const children = (node) => {
  if (!node || typeof node !== 'object') return [];
  switch (node.kind) {
    case 'pipeline': return [node.source, ...node.steps];
    case 'call': return node.args;
    case 'named': return [node.value];
    case 'method': return [node.target, ...node.args];
    case 'binary': return [node.left, node.right];
    case 'ternary': return [node.cond, node.then, node.otherwise];
    case 'unary': return [node.operand];
    case 'list': return node.items;
    case 'record': return node.fields.map((f) => f.value);
    case 'field': return [node.target];
    case 'rangeexpr': return [node.from, node.to];
    case 'bakevalue': return [node.expr, node.value];
    case 'block': return node.statements;
    case 'let': case 'assign': return [node.value];
    case 'if': return [...node.branches.flatMap((b) => [b.cond, b.body]), ...(node.otherwise ? [node.otherwise] : [])];
    case 'for': return [node.from, ...(node.to ? [node.to] : []), node.body];
    case 'repeat': return [node.count, node.body];
    case 'while': return [node.cond, ...(node.max ? [node.max] : []), node.body];
    case 'return': return node.value ? [node.value] : [];
    case 'try': return [node.body, ...node.catches.map((c) => c.body), ...(node.finally ? [node.finally] : [])];
    case 'catch': return [node.body];
    case 'emit': case 'exprstmt': return [node.value];
    case 'observe': return [...node.positional, ...node.named];
    case 'helper': return [...node.params.flatMap((p) => (p.default ? [p.default] : [])), ...(node.baked ? [node.baked] : []), node.body];
    case 'constdecl': return [node.value];
    default: return [];
  }
};
/** The statements that decide the outcome: they are part of the meaning, unlike an observation. */
const SEMANTIC_STATEMENTS = new Set(['fail', 'require', 'throw']);
const isObservation = (n) => n?.kind === 'observe' && !SEMANTIC_STATEMENTS.has(n.name);

const descendants = (node, includeObservation = true) => {
  const out = [];
  const walk = (n) => {
    if (!includeObservation && isObservation(n)) return;
    for (const child of children(n)) { out.push(child); walk(child); }
  };
  walk(node);
  return out;
};

const BUILTIN_TYPE_NAMES = ['Error', 'RequirementError', 'Record', 'Int', 'Float', 'Number', 'Bool', 'String', 'Length', 'Time', 'Duration', 'Interval', 'Gain', 'Percent',
    'Notes', 'Note', 'RandomSource', 'Seed', 'StableSeed', 'RandomStream', 'Text'];
const VOCABULARY = new Set(Object.values(NOTE_TYPES).flatMap((t) => t.values || []).map((v) => v.toLowerCase()));
const levenshtein = (a, b) => { const row = [...Array(b.length + 1).keys()]; for (let i = 1; i <= a.length; i++) { let prev = row[0]; row[0] = i; for (let j = 1; j <= b.length; j++) { const keep = row[j]; row[j] = Math.min(row[j] + 1, row[j - 1] + 1, prev + (a[i - 1] === b[j - 1] ? 0 : 1)); prev = keep; } } return row[b.length]; };
const nearest = (word, candidates) => { let best = null, bestD = 3; for (const c of candidates) { const d = levenshtein(word.toLowerCase(), c.toLowerCase()); if (d < bestD) { best = c; bestD = d; } } return best; };
const withArticle = (name) => (name && 'AEIOU'.includes(name[0]) ? 'an ' : 'a ') + name;

/** What a literal `return` cannot be, given the declared type: a short description of the literal, or null when it can be that or when nothing is known. */
const literalMismatch = (declared, value) => {
  const type = declared.trim();
  const numeric = ['Int', 'Float', 'Number', 'Count', 'Octave', 'Velocity', 'Pitch', 'Program', 'Probability', 'Interval', 'Length', 'Time', 'Duration', 'Gain', 'Percent'].includes(type);
  const word = type === 'ScaleMode' || type === 'PitchClass';
  const isString = value.kind === 'string', isBool = value.kind === 'bool', isNumber = value.kind === 'quantity';
  const literal = isString ? `"${value.value}" (a String)` : isNumber ? `${value.text} (a number)` : isBool ? `${value.value ? 'true' : 'false'} (a Bool)` : null;
  if (!literal) return null;
  if (numeric && (isString || isBool)) return literal;
  if (word && (isString || isBool || isNumber)) return literal;
  if (type === 'Notes' && (isString || isBool || isNumber)) return literal;
  if (type === 'Bool' && (isString || isNumber)) return literal;
  if ((type === 'String' || type === 'Text') && (isBool || isNumber)) return literal;
  return null;
};

/** Whether every path of the block ends with a value: a return with a value, an expression, an emit, or an if with an else where every branch does. */
const yields = (block) => {
  const last = [...block.statements].reverse().find((x) => !isObservation(x));
  if (!last) return false;
  if (last.kind === 'observe' && (last.name === 'fail' || last.name === 'throw')) return true;   // the path ends the evaluation (a thrown error goes to a catch or ends it)
  if (last.kind === 'try') return yields(last.body) && last.catches.every((c) => yields(c.body));
  if (last.kind === 'return') return !!last.value;
  if (last.kind === 'exprstmt' || last.kind === 'emit') return true;
  if (last.kind === 'if' && last.otherwise) return last.branches.every((b) => yields(b.body)) && yields(last.otherwise);
  return false;
};

/** The names, return types, paths and statements of a helper body, in every branch (also the ones that never run). Pushes issues shaped like the ones of the table. */
function checkBody(decl, ctx, issues) {
  const h = decl.node;
  const push = (id, message, start, end, extra = {}) => issues.push({ line: decl.line, issue: { id, message, start, end, ...extra } });
  const scopes = [new Map()];
  const variables = [];
  for (const p of h.params) scopes[0].set(p.name, { used: true });
  const find = (name) => { for (let i = scopes.length - 1; i >= 0; i--) if (scopes[i].has(name)) return scopes[i].get(name); return null; };
  const variableNames = () => [...scopes.flatMap((s) => [...s.keys()]), ...ctx.constants.keys()];
  const knownWord = (w) => VOCABULARY.has(w.toLowerCase()) || /^[A-G][#b]?(-?[0-9])?$/.test(w) || ctx.constants.has(w.toLowerCase()) || ctx.helpers.has(w.toLowerCase()) || ctx.types.has(w.toLowerCase())
    || [...ctx.types.values()].some((t) => (t.members || []).some((m) => String(m).toLowerCase() === w.toLowerCase()))
    || !!ctx.errorTypes?.has(w.toLowerCase()) || BUILTIN_TYPE_NAMES.includes(w) || !!NOTE_TYPES[w] || w === 'true' || w === 'false';
  const undefinedName = (w, arithmetic) => {
    const near = nearest(w.text, [...variableNames(), ...VOCABULARY]);
    push('undefined-name', `"${w.text}" is not defined here: it is not a parameter or a variable of "${h.name}", a constant, or a word the language knows (a plain word would be a symbol).${near ? ` Did you mean "${near}"?` : ''}`, w.start, w.end, { suggestion: near, strict: arithmetic });
  };
  const expr = (n, arithmetic = false) => {
    if (!n || typeof n !== 'object') return;
    switch (n.kind) {
      case 'word': {
        const local = find(n.text);
        if (local) local.used = true;
        else if ((arithmetic || !knownWord(n.text)) && !n.escaped && !n.text.includes('-')) undefinedName(n, arithmetic);
        return;
      }
      case 'reference': { const holder = find(n.block); if (holder) holder.used = true; return; }   // r.steps: a field of a variable
      case 'call': for (const a of n.args) expr(a.kind === 'named' ? a.value : a); return;
      case 'binary': {
        if (n.op === 'is') { expr(n.left); return; }
        const arith = ['+', '-', '*', '/', '%', '<', '>', '<=', '>='].includes(n.op);
        expr(n.left, arith); expr(n.right, arith);
        return;
      }
      case 'named': expr(n.value); return;
      default: for (const c of children(n)) expr(c);
    }
  };
  const block = (b, loopVar = null) => {
    scopes.push(new Map());
    if (loopVar) scopes[scopes.length - 1].set(loopVar, { used: true });
    b.statements.forEach((s, i) => {
      switch (s.kind) {
        case 'let': { expr(s.value); const local = { used: false }; scopes[scopes.length - 1].set(s.name, local); variables.push({ name: s.name, at: s, local }); break; }
        case 'assign': {
          expr(s.value);
          const target = find(s.name);
          if (target) target.used = true;
          else push('undefined-name', `"${s.name}" is not a variable here: declare it with var ${s.name} = ... first`, s.start, s.start + s.name.length, { strict: true });
          break;
        }
        case 'if': for (const br of s.branches) { expr(br.cond); block(br.body); } if (s.otherwise) block(s.otherwise); break;
        case 'for': expr(s.from); expr(s.to); block(s.body, s.var); break;
        case 'repeat': expr(s.count); block(s.body); break;
        case 'while': expr(s.cond); expr(s.max); block(s.body); break;
        case 'return': expr(s.value); break;
        case 'try': block(s.body); for (const c of s.catches) block(c.body, c.name); if (s.finally) block(s.finally); break;
        case 'emit': expr(s.value); break;
        case 'observe': for (const value of children(s)) expr(value); break;
        case 'exprstmt':
          expr(s.value);
          if (i < b.statements.length - 1 && ![s.value, ...descendants(s.value)].some((x) => ['call', 'pipeline', 'method'].includes(x.kind))) push('no-effect', 'This statement does nothing: its value is not used. Write return, emit or let in front of it, or remove it', s.start, s.end);
          break;
        default: break;
      }
    });
    scopes.pop();
  };
  block(h.body);
  for (const v of variables.filter((x) => !x.local.used)) push('unused-variable', `"${v.name}" is never used`, v.at.start, v.at.start + 4 + v.name.length);
  if (h.returns) {
    for (const r of descendants(h.body).filter((x) => x.kind === 'return' && x.value)) {
      const what = literalMismatch(h.returns, r.value);
      if (what) push('type-mismatch', `${what} cannot be returned by "${h.name}": it says it returns ${withArticle(h.returns)}`, r.value.start, r.value.end);
    }
    if (!descendants(h.body).some((x) => x.kind === 'emit') && !yields(h.body)) push('no-value', `"${h.name}" can end without a value: add an else with a return, or end the helper with an expression`, h.nameStart ?? h.start, h.nameEnd ?? h.start + h.name.length);
  }
}

const DIAGNOSTIC_SEVERITY = {
  'unused-helper': 3, 'helper-broken': 3, 'undefined-name': 2, 'type-mismatch': 1, 'no-value': 2, 'unused-variable': 3, 'no-effect': 3,
  syntax: 1, 'error-decl': 1, 'feature-off': 1, 'reserved-name': 2, 'duplicate-helper': 1,
};
const DIAGNOSTIC_CODE = {
  'unused-helper': 'AMP2086', 'helper-broken': 'AMP2087', 'undefined-name': 'AMP2082', 'type-mismatch': 'AMP2044', 'no-value': 'AMP2083',
  'unused-variable': 'AMP2084', 'no-effect': 'AMP2085', syntax: 'AMP1111', 'error-decl': 'AMP6040', 'feature-off': 'AMP6020', 'reserved-name': 'AMP6016', 'duplicate-helper': 'AMP2064',
};

/**
 * The issues of the helper table of a song text as diagnostics (LSP shape: range, severity 1 error / 2 warning / 3 note, code, message), for the editor when the language server cannot be reached.
 * It finds the same things as the server for the declarations (the corpus spec-tests/note-lang/static.json is run by both) and a helper that nothing in the song calls (AMP2086); the call of a broken helper (AMP2087)
 * is found when a pipeline is worked out (note-lang.mjs: callOf).
 */
export function helperDiagnostics(text, table, sources = helperSourcesOf(text)) {
  const found = table.issues.filter(({ issue }) => DIAGNOSTIC_CODE[issue.id]).map(({ line, issue }) => {
    const decl = sources.find((s) => s.line === line)?.text ?? '';
    const at = (offset) => {
      const before = decl.slice(0, Math.min(Math.max(0, offset), decl.length));
      const breaks = before.split('\n').length - 1;
      return { line: line + breaks, character: breaks === 0 ? before.length : before.length - before.lastIndexOf('\n') - 1 };
    };
    return { range: { start: at(issue.start), end: at(issue.end) }, severity: issue.strict ? 1 : DIAGNOSTIC_SEVERITY[issue.id] ?? 2, code: DIAGNOSTIC_CODE[issue.id], source: 'ample (browser)', message: issue.message + '.' };
  });
  return [...found, ...unusedHelpers(text, table, sources)];
}

import { HEADER_TAIL } from './block-header.mjs';

const BLOCK_HEADER = new RegExp(String.raw`^\s*[A-Za-z][\w-]*\s+[A-Za-z_][\w-]*` + HEADER_TAIL);

/** AMP2086 (the browser twin of the parser): a helper that nothing else in the song mentions. Only in a song (a library of helpers has no blocks to call them); comments do not count. */
function unusedHelpers(text, table, sources) {
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  if (!lines.some((l) => BLOCK_HEADER.test(l))) return [];
  const out = [];
  for (const d of table.declarations?.() ?? []) {
    if (d.kind !== 'helper') continue;
    const decl = sources.find((s) => s.line === d.line)?.text ?? '';
    const first = d.line, last = first + decl.split('\n').length - 1;
    const use = new RegExp('(?<![\\w-])' + d.name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '(?![\\w-])', 'i');
    let called = false;
    for (let k = 0; k < lines.length && !called; k++) {
      if (k >= first && k <= last) continue;
      const code = lines[k].includes('#') ? lines[k].slice(0, lines[k].indexOf('#')) : lines[k];
      called = use.test(code);
    }
    if (called) continue;
    const from = Math.max(0, decl.indexOf(d.name)), to = from + d.name.length;
    const at = (offset) => { const before = decl.slice(0, offset); const breaks = before.split('\n').length - 1; return { line: d.line + breaks, character: breaks === 0 ? before.length : before.length - before.lastIndexOf('\n') - 1 }; };
    out.push({ range: { start: at(from), end: at(to) }, severity: DIAGNOSTIC_SEVERITY['unused-helper'], code: DIAGNOSTIC_CODE['unused-helper'], source: 'ample (browser)', message: `"${d.name}" is never called in this song.`, data: { hint: 'Call it from a clip or a property, remove it, or silence this with # ample-ignore AMP2086 on the line above.' } });
  }
  return out;
}

/**
 * The declarations of a file: `sources` is [{ text, line }] and `parse` the declaration parser of note-pipeline.mjs. Names resolve ignoring case. The effect of a helper (pure or dynamic) is worked out from its body
 * and the helpers it calls, a baked helper is pure for its callers.
 */
const RESERVED_WORDS = new Set(['log', 'debug', 'trace', 'warn', 'assert', 'require', 'throw', 'fail', 'try', 'catch', 'finally', 'error']);

export function buildHelperTable(sources, parse) {
  const helpers = new Map(), constants = new Map(), types = new Map(), errorTypes = new Map(), issues = [], failed = new Set();
  const all = [];
  for (const { text, line } of sources) {
    const { ast, errors } = parse(text);
    if (!ast) {
      for (const e of errors) issues.push({ line, issue: e });
      // a helper whose declaration cannot be read: a call of it says so (AMP2087) instead of 'unknown operator'
      const broken = /^\s*(?:dynamic\s+)?helper\s+([A-Za-z_][\w-]*)/.exec(text);
      if (broken) failed.add(broken[1].toLowerCase());
      continue;
    }
    const name = ast.name;
    const key = name.toLowerCase();
    const builtinError = key === 'error' || key === 'requirementerror';
    const taken = ast.kind === 'errordecl' ? errorTypes.has(key) || types.has(key) || builtinError
      : ast.kind === 'typedecl' ? types.has(key) || errorTypes.has(key) : helpers.has(key) || constants.has(key);
    if (taken) { issues.push({ line, issue: { id: 'duplicate-helper', message: `"${name}" is declared twice`, start: ast.start, end: ast.end } }); continue; }
    if (ast.kind !== 'typedecl' && RESERVED_WORDS.has(key)) {
      const message = `"${name}" is a word that AMPLE reserves for statements (log, debug, trace, warn, assert, require, throw, fail, try, catch, finally, error): `
        + 'a statement written with it will not call this helper or constant, so give it another name';
      issues.push({ line, issue: { id: 'reserved-name', message, start: ast.nameStart ?? ast.start, end: ast.nameEnd ?? ast.end } });
    }
    const decl = { node: ast, name, line, text, dynamic: ast.kind === 'helper' && ast.dynamic, effect: 'pure' };
    if (ast.kind === 'typedecl') types.set(key, ast); else if (ast.kind === 'errordecl') errorTypes.set(key,
        ast); else if (ast.kind === 'constdecl') constants.set(key, decl); else helpers.set(key, decl);
    all.push(decl);
  }
  /** The error type with its chain (the type first, then its parents, ending in Error) and all fields, parents first; null when the name is not an error type. */
  const errorSpec = (name) => {
    const lower = String(name).toLowerCase();
    if (lower === 'error') return { chain: ['Error'], fields: [] };
    if (lower === 'requirementerror') return { chain: ['RequirementError', 'Error'], fields: [] };
    const chain = [], fields = [];
    let at = lower;
    while (at && errorTypes.has(at)) {
      const node = errorTypes.get(at);
      if (chain.some((c) => c.toLowerCase() === at)) return null;
      chain.push(node.name);
      fields.unshift(...node.fields);
      at = node.parent ? node.parent.toLowerCase() : null;
    }
    if (!chain.length) return null;
    if (at === 'requirementerror') chain.push('RequirementError');
    chain.push('Error');
    return { chain, fields };
  };
  const isErrorType = (name) => ['error', 'requirementerror'].includes(String(name).toLowerCase()) || errorTypes.has(String(name).toLowerCase());
  const ERROR_MEMBERS = new Set(['type', 'message', 'code', 'hint', 'origin', 'context', 'cause', 'suppressed', 'suppressedCount']);
  for (const d of all.filter((x) => x.node.kind === 'errordecl')) {
    const e = d.node;
    const problem = (message) => issues.push({ line: d.line, issue: { id: 'error-decl', message, start: e.start, end: e.end } });
    if (e.parent && !isErrorType(e.parent)) problem(`"${e.name}" extends "${e.parent}", which is not an error type (Error, RequirementError or an error declared in this file)`);
    else {
      const spec = errorSpec(e.name);
      if (!spec) problem(`"${e.name}" extends itself through its parents`);
      else for (const name of new Set(spec.fields.map((f) => f.name))) if (spec.fields.filter((f) => f.name === name).length > 1) problem(`The field "${name}" is declared twice (also by a parent)`);
    }
    for (const f of e.fields) if (ERROR_MEMBERS.has(f.name)) problem(`"${f.name}" is a member every error has: choose another field name`);
  }

  const baked = (d) => d.node.kind === 'helper' && d.node.baked;
  const directCalls = (declaration) => descendants(declaration.node, false).flatMap((node) => {
    if (node.kind === 'call') return [node.name.toLowerCase()];
    if (node.kind === 'word' && constants.has(node.text.toLowerCase())) return [node.text.toLowerCase()];
    return [];
  });
  const calls = new Map(all.map((declaration) => [declaration, new Set(directCalls(declaration))]));
  const dynamicFields = new Set([...errorTypes.values()].flatMap((e) => e.fields.filter((f) => f.dynamic).map((f) => f.name)));
  /** Whether a type of an error (itself or a type that extends it) declares this field as dynamic: a value of that type may carry it. */
  const mayHaveDynamicField = (type, field) => {
    if (!isErrorType(type)) return false;   // a record, a number, notes ...: not an error, so no declared field
    const wanted = String(type).toLowerCase();
    return [...errorTypes.values()].some((e) => {
      const spec = errorSpec(e.name);
      return spec && spec.chain.some((c) => c.toLowerCase() === wanted) && spec.fields.some((f) => f.dynamic && f.name === field);
    });
  };
  /** The type of the receiver decides when it is known; when it is not, the field name decides (it can only ask for dynamic once too often, never once too little). */
  const readsDynamicFieldOf = (d) => {
    const types = new Map();
    const note = (name, type) => { if (!types.has(name)) types.set(name, type); else if (String(types.get(name)).toLowerCase() !== String(type).toLowerCase()) types.set(name, null); };
    const infer = (value) => {
      if (!value) return null;
      if (value.kind === 'call' && isErrorType(value.name)) return value.name;
      if (value.kind === 'record') return 'Record';
      if (value.kind === 'word' && types.has(value.text)) return types.get(value.text);
      if (['string', 'quantity', 'bool', 'list'].includes(value.kind)) return 'Value';
      return null;
    };
    for (const p of d.node.params ?? []) note(p.name, p.type);
    const nodes = descendants(d.node, false);
    for (const n of nodes) {
      if (n.kind === 'let') note(n.name, n.type ?? infer(n.value));
      else if (n.kind === 'assign') note(n.name, infer(n.value));
      else if (n.kind === 'try') { for (const c of n.catches) note(c.name, c.type ?? 'Error'); }
      else if (n.kind === 'for') note(n.var, null);
    }
    const reads = (variable, field) => {
      if (!dynamicFields.has(field)) return false;
      const type = variable !== undefined && types.has(variable) ? types.get(variable) : null;
      return type === null || mayHaveDynamicField(type, field);
    };
    return nodes.some((n) => (n.kind === 'field' ? reads(n.target?.kind === 'word' ? n.target.text : undefined, n.name) : n.kind === 'reference' ? reads(n.block, n.port) : false));
  };
  const readsDynamicField = new Set(dynamicFields.size ? all.filter((d) => readsDynamicFieldOf(d)) : []);
  for (const d of all) d.effect = d.dynamic && !baked(d) ? 'dynamic' : 'pure';
  for (let changed = true; changed;) {
    changed = false;
    for (const d of all) {
      const volatileCall = () => [...calls.get(d)].some((n) => VOLATILE.includes(n) || helpers.get(n)?.effect === 'dynamic' || constants.get(n)?.effect === 'dynamic');
      if (d.effect === 'pure' && !baked(d) && (readsDynamicField.has(d) || volatileCall())) { d.effect = 'dynamic'; changed = true; }
    }
  }
  for (const d of all) if (d.node.kind === 'helper') checkBody(d, { helpers, constants, types, errorTypes }, issues);
  return {
    issues,
    helper: (name) => { const d = helpers.get(String(name).toLowerCase()); return d ? { node: d.node, canonical: d.name, effect: d.effect } : null; },
    constant: (name) => { const d = constants.get(String(name).toLowerCase()); return d ? d.node : null; },
    type: (name) => types.get(String(name).toLowerCase())?.members ?? null,
    names: () => [...helpers.values()].map((d) => d.name),
    /** The helpers as signatures, shaped like the catalogue of the operators (name, receiver, params with type and default, returns, effect), for completion and signature help. */
    signatures: () => Object.fromEntries([...helpers.values()].filter((d) => d.node.kind === 'helper').map((d) => [d.name, {
      id: d.name, kind: 'helper', receiver: d.node.params[0]?.type === 'Notes' ? 'Notes' : null, returns: d.node.returns ?? 'Notes', returnsDeclared: !!d.node.returns, effect: d.effect, line: d.line,
      doc: (d.effect === 'dynamic' ? 'dynamic helper' : 'helper') + (d.node.baked ? ' (baked)' : ''),
      params: d.node.params.map((p) => ({ name: p.name, type: p.type, required: !p.default, ...(p.default ? { default: d.text.slice(p.default.start, p.default.end).replace(/\s+/g, ' ') } : {}) })),
    }])),
    constants: () => [...constants.values()].map((d) => ({ name: d.name, type: d.node.type, line: d.line })),
    isDynamic: (name) => helpers.get(String(name).toLowerCase())?.effect === 'dynamic',
    errorSpec,
    isErrorType,
    isBroken: (name) => failed.has(String(name).toLowerCase()),
    declarations: () => all.map((d) => ({ name: d.name, line: d.line, kind: d.node.kind })),
    /** The declarations with their trees and texts (for the types of the variables, variable-types.mjs). */
    all: () => all.map((d) => ({ name: d.name, line: d.line, node: d.node, text: d.text })),
    get empty() { return helpers.size === 0 && constants.size === 0 && types.size === 0; },
  };
}

/** Whether the tree needs the language layer: arithmetic, lists, helpers, constants, value bakes. */
export function needsLang(ast, table, operatorNames) {
  const nodes = [ast, ...descendants(ast)];
  return nodes.some((n) => ['binary', 'unary', 'list', 'bool', 'field', 'rangeexpr', 'bakevalue'].includes(n.kind)
    || (n.kind === 'call' && !operatorNames.includes(n.name) && !['random', 'choose', 'chance', 'stable', 'random-seed'].includes(n.name))
    || (n.kind === 'word' && table && table.constant(n.text)));
}

const isVolatileNode = (root, table) => [root, ...descendants(root, false)]
  .some((n) => n.kind === 'call' && (VOLATILE.includes(n.name.toLowerCase()) || table.isDynamic(n.name)));

// ---------- the interpreter

/** The failures that a catch can handle (class T1 of spec/failure-classes.json, checked by a test) with their stable codes. */
const CATCHABLE_CODES = { 'divide-by-zero': 'DIV_ZERO', 'unknown-field': 'UNKNOWN_FIELD', conversion: 'CONVERSION', throw: 'THROW', require: 'REQUIRE' };
/** The safety stops (class T3): no user code runs after them, so no finally either. */
const SAFETY_STOPS = new Set(['fuel', 'loop-limit', 'while-limit', 'stack-depth', 'memory', 'cap-events', 'cap-time', 'cancelled']);
const UNWIND_FUEL = 5000, UNWIND_DEPTH = 8;

class Failure extends Error { constructor(id, message, node, suggestion = null) { super(message); this.id = id; this.node = node; this.suggestion = suggestion; } }
const article = (name) => (name && 'AEIOU'.includes(name[0]) ? 'an ' : 'a ') + name;

/**
 * Runs an expression tree that needs the language layer. `kit` brings what the evaluator of the note pipelines owns: its operators (step, generate, valueCall, evalNode), the exact maths and the problem lists.
 * Returns the notes value ({ events, span }) or null (the problems are in kit.errors).
 */
export function runLang(ast, kit) {
  const { errors, warnings, bpm, math, table, text } = kit;
  const { rat, mul, add, sub, isInt, roundHalfEven, parseDecimal, toMicro } = math;
  const limits = { ...DEFAULT_LIMITS, ...(kit.limits || {}) };
  const BEAT = kit.BEAT;
  let fuel = 0, depth = 0, noteBytes = 0, dynamic = false, bakedDynamic = false;
  let memoryLimitMb = limits.memoryMb;
  const dynamicInputs = [];
  const constantCache = new Map(), busy = new Set();

  let fuelLimit = limits.fuel, depthLimit = limits.stackDepth, reserveExhausted = false, inReserve = false;
  const handling = [];
  let inAssertion = false, assertionSpent = 0;
  const ASSERTION_STATEMENT_FUEL = 50_000, ASSERTION_SESSION_FUEL = 1_000_000, ASSERTION_DEPTH = 16, ASSERTION_MEMORY_MB = 16;
  const ASSERTION_UNAVAILABLE = new Set(['fuel', 'stack-depth', 'memory', 'loop-limit', 'while-limit', 'cap-events', 'unknown-reference']);
  const tick = (n) => { if (++fuel > fuelLimit) throw new Failure('fuel', `The helper code used more than ${fuelLimit} evaluation steps (the limit of the song is limits.fuel)`, n); };
  const account = (v, n) => {
    if (v.k !== 'notes') return;
    noteBytes += v.v.events.length * 64;
    if (noteBytes > memoryLimitMb * 1048576) {
      throw new Failure('memory', `The notes made by this song take more than ${memoryLimitMb} MB (an estimate; the limit of the song is limits.memory)`, n);
    }
  };
  const warn = (id, message, node, suggestion) => warnings.push({ id, message, start: node?.start ?? 0, end: node?.end ?? text.length, suggestion });

  // ----- values
  const INT = (v, brand = null) => ({ k: 'int', v: BigInt(v), brand });
  const FLOAT = (v, exact = null) => ({ k: 'float', v, exact });
  const withTaint = (v, t) => (t && !v.tainted ? { ...v, tainted: true } : v);
  // a secret (a value read with environment(), and everything computed from it) is never written into a message: the twin of ValueMarks.Sensitive
  const REDACTED = '[redacted]';
  const deepSecret = (v) => !!v.secret
    || (v.k === 'list' && v.items.some(deepSecret))
    || (v.k === 'range' && (deepSecret(v.from) || deepSecret(v.to)))
    || (v.k === 'record' && v.fields.some(([, x]) => deepSecret(x)))
    || (v.k === 'error' && ((v.context && deepSecret(v.context)) || (v.cause && deepSecret(v.cause))));
  const withSecret = (v, s) => (s && !v.secret ? { ...v, secret: true } : v);
  // whether a value, or anything inside it, may vary between runs (provenance; the monotonic dynamic flag stays the authority for what is built again)
  const deepTainted = (v) => !!v.tainted
    || (v.k === 'list' && v.items.some(deepTainted))
    || (v.k === 'range' && (deepTainted(v.from) || deepTainted(v.to)))
    || (v.k === 'record' && v.fields.some(([, x]) => deepTainted(x)))
    || (v.k === 'error' && ((v.context && deepTainted(v.context)) || (v.cause && deepTainted(v.cause))));
  // above 0 while a branch, a loop body or a catch that a tainted decision selected is running: everything it makes is tainted
  let control = 0, decisionTainted = false, wholeTainted = false, argTaint = false;
  const ctl = (v) => (control > 0 ? withTaint(v, true) : v);
  const selected = (block, env, frame, tainted) => {
    if (tainted) control++;
    try { return execBlock(block, env, frame); } catch (e) { if (tainted && e instanceof Failure) e.tainted = true; throw e; } finally { if (tainted) control--; }
  };
  const ratOfDouble = (d) => {
    if (d === 0 || !Number.isFinite(d)) return rat(0n);
    const buf = new DataView(new ArrayBuffer(8)); buf.setFloat64(0, d);
    const bits = buf.getBigUint64(0), negative = (bits >> 63n) === 1n;
    let exponent = Number((bits >> 52n) & 0x7ffn), mantissa = bits & 0xfffffffffffffn;
    if (exponent === 0) exponent++; else mantissa |= 1n << 52n;
    exponent -= 1075;
    let n = mantissa, den = 1n;
    if (exponent > 0) n <<= BigInt(exponent); else den <<= BigInt(-exponent);
    return rat(negative ? -n : n, den);
  };
  const doubleOfRat = (r) => Number(r.n) / Number(r.d);
  const asRat = (v) => (v.k === 'int' ? rat(v.v) : v.exact ?? ratOfDouble(v.v));
  const asDouble = (v) => (v.k === 'int' ? Number(v.v) : v.v);
  const isNumber = (v) => v.k === 'int' || v.k === 'float';
  const decimalText = (r, places = 9) => {
    const n = r.n < 0n ? -r.n : r.n, d = r.d, sign = r.n < 0n ? '-' : '';
    let whole = n / d, rest = n % d;
    if (rest === 0n) return sign + String(whole);
    let out = sign + String(whole) + '.';
    for (let i = 0; i < places && rest !== 0n; i++) { rest *= 10n; out += String(rest / d); rest %= d; }
    return out.replace(/0+$/, '').replace(/\.$/, '');
  };
  const typeName = (v) => ({ int: 'Int', float: 'Float', bool: 'Bool', str: 'String', notes: 'Notes', note: 'Note', list: 'List', record: 'Record', error: 'Error',
      range: 'Range', word: 'a word', seed: 'RandomSource', now: 'a date and time' }[v.k] || (v.k === 'quant' ? { length: 'Length', time: 'Time',
      interval: 'Interval', percent: 'Percent' }[v.dim] || 'Gain' : 'nothing'));
  const valText = (v) => {
    switch (v.k) {
      case 'int': return String(v.v);
      case 'float': return v.exact ? decimalText(v.exact) : String(v.v);
      case 'bool': return v.v ? 'true' : 'false';
      case 'str': return `"${v.v}"`;
      case 'quant': return v.dim === 'length' ? `${toMicro(v.v, 'beats', bpm) ?? 0}ticks` : v.dim === 'time' ? `${decimalText(v.v, 6)}s` : v.dim === 'interval' ? `${decimalText(v.v, 6)}st` : v.dim === 'percent' ? `${decimalText(mul(v.v, rat(100n)), 6)}%` : `${decimalText(v.v, 6)}dB`;
      case 'word': return v.v;
      case 'seed': return v.text;
      case 'now': return v.iso;
      case 'list': return `[${v.items.map(valText).join(', ')}]`;
      case 'record': return `{${v.fields.map(([key, x]) => `${key}: ${valText(x)}`).join(', ')}}`;
      case 'error': return `${v.type}(${v.message})`;
      case 'range': return valText(v.from) + (v.inclusive ? '..=' : '..') + valText(v.to);
      case 'notes': return 'notes';
      default: return 'nothing';
    }
  };
  const safeText = (v) => (deepSecret(v) ? REDACTED : valText(v));
  const describe = (v) => (v.k === 'notes' ? 'Notes' : `${safeText(v)} (${typeName(v)})`);

  // ----- text: the one rule behind +, String(value) and sprintf (the twin of NoteLang.Text.cs)
  const MAX_TEXT = 1048576, MAX_FORMAT_WIDTH = 64, MAX_FORMAT_PRECISION = 20;
  /** The text of a single value: a text as it is, any other single value as the log writes it; null for a list, record, notes, error, range, date or nothing. */
  const scalarText = (v) => (v.k === 'str' ? v.v : v.k === 'quant' && v.dim === 'length' ? decimalText(v.v, 6) + (v.v.n === 1n && v.v.d === 1n ? 'beat' : 'beats')
    : ['int', 'float', 'bool', 'quant', 'word'].includes(v.k) ? valText(v) : null);
  const madeText = (text, at) => {
    if (text.length > MAX_TEXT) throw new Failure('memory', `A text made here would be ${text.length} characters long; a text may be at most ${MAX_TEXT} characters`, at);
    fuel += Math.floor(text.length / 256);
    if (fuel > fuelLimit) throw new Failure('fuel', `The helper code used more than ${fuelLimit} evaluation steps (the limit of the song is limits.fuel)`, at);
    return { k: 'str', v: text };
  };
  const article = (name) => ('AEIOU'.includes(name[0]) ? 'an ' : 'a ') + name;
  const joinText = (op, l, r, at) => {
    if (op !== '+' || (l.k !== 'str' && r.k !== 'str')) return null;
    const joinable = (v) => v.k === 'str' || v.k === 'int' || v.k === 'float';
    if (joinable(l) && joinable(r)) return madeText(scalarText(l) + scalarText(r), at);
    const other = l.k === 'str' ? r : l;
    throw new Failure('type-mismatch', `+ joins a text with a text or a number, not with ${describe(other)}: write String(...) around it to turn it into text first`, at);
  };
  const toStringValue = (v, at) => {
    const text = scalarText(v);
    if (text === null) throw new Failure('conversion', `String cannot turn ${article(v.k === 'notes' ? 'Notes' : typeName(v))} into text: it takes a text, a number, a Bool or a quantity (log the value itself to see a list or record)`, at);
    return madeText(text, at);
  };
  const fixedText = (r, places) => {
    const scale = 10n ** BigInt(places);
    const q = roundHalfEven(rat(r.n * scale, r.d));
    const negative = q < 0n;
    const digits = String(negative ? -q : q).padStart(places + 1, '0');
    return (negative ? '-' : '') + (places > 0 ? digits.slice(0, -places) + '.' + digits.slice(-places) : digits);
  };
  const sprintf = (args, c) => {
    if (args.length === 0 || args[0].value.k !== 'str') throw new Failure('bad-argument', 'sprintf needs a format text first, like sprintf("Bar %d", 4)', args.length === 0 ? c : args[0].origin);
    const named = args.find((a) => a.name !== null);
    if (named) throw new Failure('bad-argument', 'sprintf takes its values in order, without names', named.origin);
    const f = args[0].value.v;
    const show = (spec) => (deepSecret(args[0].value) ? 'a placeholder' : `"${spec}"`);   // a secret format never shows a piece of itself
    const badFormat = (why) => new Failure('bad-argument', 'sprintf: ' + why, args[0].origin);
    const digit = (ch) => ch >= '0' && ch <= '9';
    let out = '', next = 1, i = 0;
    while (i < f.length) {
      const ch = f[i];
      if (ch !== '%') { out += ch; i++; continue; }
      const start = i++;
      let flag = '';
      if (i < f.length && (f[i] === '-' || f[i] === '0')) flag = f[i++];
      const widthStart = i;
      while (i < f.length && digit(f[i]) && i - widthStart < 9) i++;
      const width = i > widthStart ? Number(f.slice(widthStart, i)) : 0;
      let precision = -1;
      if (i < f.length && f[i] === '.') {
        const precisionStart = ++i;
        while (i < f.length && digit(f[i]) && i - precisionStart < 9) i++;
        if (i === precisionStart) throw badFormat('a precision needs digits after the point, like %.2f');
        precision = Number(f.slice(precisionStart, i));
      }
      if (i >= f.length) throw badFormat('the format ends inside a placeholder (write %% for a percent sign)');
      const conv = f[i++];
      const spec = f.slice(start, i);
      if (conv === '%') {
        if (i - start !== 2) throw badFormat(`${show(spec)} is not a placeholder: write %% alone for a percent sign`);
        out += '%';
        continue;
      }
      if (conv !== 's' && conv !== 'd' && conv !== 'f') throw badFormat(`${show(spec)} is not supported (supported: %s, %d, %f, %.Nf and %%, with an optional - or 0 and a width)`);
      if (width > MAX_FORMAT_WIDTH) throw badFormat(`the width of ${show(spec)} is more than ${MAX_FORMAT_WIDTH}`);
      if (precision > MAX_FORMAT_PRECISION) throw badFormat(`the precision of ${show(spec)} is more than ${MAX_FORMAT_PRECISION}`);
      if (precision >= 0 && conv !== 'f') throw badFormat(`${show(spec)}: a precision is only for %f`);
      if (flag === '0' && conv === 's') throw badFormat(`${show(spec)}: zero padding is only for numbers`);
      if (next >= args.length) throw new Failure('bad-argument', `sprintf: the format has more placeholders than the ${args.length - 1} value${args.length === 2 ? '' : 's'} given`, c);
      const arg = args[next++], v = arg.value;
      let piece;
      if (conv === 's') {
        piece = scalarText(v);
        if (piece === null) throw new Failure('bad-argument', `sprintf: %s takes a text, a number, a Bool or a quantity, not ${v.k === 'notes' ? 'Notes' : article(typeName(v))}`, arg.origin);
      } else if (conv === 'd') {
        if (v.k !== 'int') throw new Failure('bad-argument', `sprintf: %d takes an Int, not ${describe(v)}: use %f, or Round(...) first`, arg.origin);
        piece = String(v.v);
      } else {
        const places = precision < 0 ? 6 : precision;
        if (v.k === 'int') piece = fixedText(rat(v.v), places);
        else if (v.k === 'float' && !Number.isFinite(v.v)) piece = String(v.v);
        else if (v.k === 'float') piece = fixedText(v.exact ?? ratOfDouble(v.v), places);
        else throw new Failure('bad-argument', `sprintf: %f takes a number, not ${describe(v)}: use %s for a quantity`, arg.origin);
      }
      const numeric = conv !== 's' && piece.length > 0 && digit(piece[piece.length - 1]);
      if (piece.length < width) {
        if (flag === '-') piece = piece.padEnd(width);
        else if (flag === '0' && numeric) { const negative = piece[0] === '-'; piece = (negative ? '-' : '') + (negative ? piece.slice(1) : piece).padStart(width - (negative ? 1 : 0), '0'); }
        else piece = piece.padStart(width);
      }
      out += piece;
      if (out.length > MAX_TEXT) break;
    }
    if (next < args.length) throw new Failure('bad-argument', `sprintf: ${args.length - 1} values were given but the format has ${next - 1} placeholder${next === 2 ? '' : 's'}`, args[next].origin);
    return madeText(out, c);
  };

  const fromQuantity = (q) => {
    const v = q.value;
    switch (q.unit) {
      case null: case undefined: return isInt(v) ? INT(v.n) : FLOAT(doubleOfRat(v), v);
      case 'bar': case 'bars': return { k: 'quant', v: mul(v, rat(4n)), dim: 'length' };
      case 'beat': case 'beats': return { k: 'quant', v, dim: 'length' };
      case 'tick': case 'ticks': return { k: 'quant', v: rat(v.n, v.d * 1000000n), dim: 'length' };
      case 's': return { k: 'quant', v, dim: 'time' };
      case 'ms': return { k: 'quant', v: mul(v, rat(1n, 1000n)), dim: 'time' };
      case 'st': return { k: 'quant', v, dim: 'interval' };
      case 'oct': return { k: 'quant', v: mul(v, rat(12n)), dim: 'interval' };
      case 'cents': return { k: 'quant', v: mul(v, rat(1n, 100n)), dim: 'interval' };
      case '%': return { k: 'quant', v: mul(v, rat(1n, 100n)), dim: 'percent' };
      case 'dB': case 'db': return { k: 'quant', v, dim: 'gain' };
      default: throw new Failure('wrong-unit', `Unknown unit "${q.unit}"`, q);
    }
  };
  const q = (value, unit, at, textOf = '') => ({ kind: 'quantity', value, unit, start: at.start, end: at.end, text: textOf });
  const nodeOf = (v, at) => {
    switch (v.k) {
      case 'int': return q(rat(v.v), null, at, String(v.v));
      case 'float': return q(v.exact ?? ratOfDouble(v.v), null, at, String(v.v));
      case 'quant': return v.dim === 'length' ? q(v.v, 'beats', at) : v.dim === 'time' ? q(v.v, 's', at) : v.dim === 'gain' ? q(v.v, 'dB', at) : q(v.v, null, at);
      case 'str': return { kind: 'string', value: v.v, start: at.start, end: at.end };
      case 'word': return { kind: 'word', text: v.v, start: at.start, end: at.end };
      case 'bool': return { kind: 'word', text: v.v ? 'true' : 'false', start: at.start, end: at.end };
      case 'seed': return v.node;
      case 'notes': return { kind: 'value', value: v.v, start: at.start, end: at.end };
      case 'range': { const from = nodeOf(v.from, at), to = nodeOf(v.to, at); if (from.kind === 'quantity' && to.kind === 'quantity') return { kind: 'range', from, to, inclusive: v.inclusive, start: at.start, end: at.end }; break; }
      default: break;
    }
    throw new Failure('type-mismatch', 'This value cannot be given to an operator here', at);
  };
  const fromNode = (n) => {
    if (n.kind === 'quantity') return fromQuantity(n);
    if (n.kind === 'string') return { k: 'str', v: n.value };
    if (n.kind === 'word') return { k: 'word', v: n.text };
    throw new Failure('type-mismatch', 'An operator gave something this layer cannot read', n);
  };

  // ----- evaluation
  const lookup = (env, name) => { for (let e = env; e; e = e.parent) if (e.vars.has(name)) return e.vars.get(name); return null; };
  const newEnv = (parent = null) => ({ vars: new Map(), parent });

  const word = (w, env) => {
    const hyphenated = w.text.includes('-');
    const known = (part) => lookup(env, part) || table.constant(part) || /^\d+$/.test(part);
    const parts = hyphenated ? w.text.split('-') : [];
    const partsExist = parts.length >= 2 && parts.every(known);
    if (hyphenated && !w.escaped && partsExist && (lookup(env, w.text) || table.constant(w.text))) {
      throw new Failure('ambiguous-name', "\"" + w.text + "\" is ambiguous: it can be the symbol " + w.text + " or the subtraction " + parts.join(' - ') + ". Write (" + w.text + ") or " + parts.join(' - ') + " to subtract, or [" + w.text + "] for the symbol", w, parts.join(' - '));
    }
    const found = lookup(env, w.text);
    if (found) return found.value;
    const decl = table.constant(w.text);
    if (decl) {
      if (constantCache.has(decl)) return constantCache.get(decl);
      if (busy.has(decl)) throw new Failure('cycle', `"${w.text}" is defined in terms of itself`, w);
      busy.add(decl);
      try { let v = evalNode(decl.value, newEnv()); if (decl.type) v = coerce(v, decl.type, decl.value, w.text); constantCache.set(decl, v); return v; } finally { busy.delete(decl); }
    }
    if (partsExist && !w.escaped) throw new Failure('unknown-name', "\"" + w.text + "\" is one name (a hyphen belongs to a name): write " + parts.join(' - ') + " or (" + w.text + ") to subtract", w, parts.join(' - '));
    if (w.escaped) throw new Failure('unknown-name', 'There is no symbol [' + w.text + ']' + (partsExist ? ': did you mean the subtraction ' + parts.join(' - ') + '?' : ''), w, partsExist ? parts.join(' - ') : null);
    return { k: 'word', v: w.text };
  };

  const field = (target, name, at) => {
    let result;
    if (target.k === 'now') {
      const t = target.t;
      const fields = { year: t.getFullYear(), month: t.getMonth() + 1, day: t.getDate(), hour: t.getHours(), minute: t.getMinutes(), second: t.getSeconds(), weekday: t.getDay() === 0 ? 7 : t.getDay() };
      if (!(name in fields)) throw new Failure('bad-argument', `A date and time has no "${name}" (it has year, month, day, hour, minute, second, weekday)`, at);
      result = INT(fields[name]);
    } else if (target.k === 'note') {
      const n = target.n;
      if (name === 'at') result = { k: 'quant', v: rat(BigInt(n.at), BigInt(BEAT)), dim: 'length' };
      else if (name === 'len') result = { k: 'quant', v: rat(BigInt(n.len), BigInt(BEAT)), dim: 'length' };
      else if (name === 'pitch') result = INT(n.pitch);
      else if (name === 'vel') result = INT(n.vel);
      else throw new Failure('bad-argument', `A note has no "${name}" (it has at, len, pitch, vel)`, at);
    } else if (target.k === 'error') {
      result = errorField(target, name, at);
    } else if (target.k === 'record') {
      const hit = target.fields.find(([key]) => key === name);
      if (!hit) throw new Failure('unknown-field', `The record has no field "${name}" (it has ${target.fields.length === 0 ? 'none' : target.fields.map(([key]) => key).join(', ')})`, at);
      result = hit[1];
    } else throw new Failure('bad-argument', `${describe(target)} has no field "${name}"`, at);
    return withSecret(withTaint(result, target.tainted), !!target.secret);
  };

  /** fail and require (plan D6-D8, WP5a): fail stops the evaluation (T2), require checks a condition that a caller may fix (T1). The message holds values only, never a position. */
  const raise = (s, env) => {
    const textOf = (v) => (v.k === 'str' ? v.v : valText(v));
    const hint = s.named.length && s.named[0].name === 'hint' ? textOf(evalNode(s.named[0].value, env)) : null;
    if (s.name === 'throw') return throwStatement(s, env);
    if (s.name === 'fail') throw new Failure('fail', textOf(evalNode(s.positional[0], env)), s, hint);
    const decided = evalNode(s.positional[0], env);
    if (truthy(decided)) return;
    const message = s.positional.length > 1 ? textOf(evalNode(s.positional[1], env)) : 'Requirement failed: ' + s.conditionText;
    const failure = new Failure('require', message, s, hint);
    if (control > 0 || deepTainted(decided)) failure.tainted = true;
    throw failure;
  };

  /**
   * Whether a value counts as true where a condition is read (if, while, ?:, !, &&, ||): true is true; a number that is not 0, a quantity that is not 0, a string or a symbol that is not empty, a list with items, a range that is not empty and
   * notes with at least one note are true-ish; 0, 0.0, 0ms, "", an empty list, an empty range and empty notes are false-ish. Seeds, random streams, times and single notes are always true. The twin of Truthy in NoteLang.Interp.cs.
   */
  const truthy = (v) => {
    switch (v.k) {
      case 'bool': return v.v;
      case 'int': return v.v !== 0n;
      case 'float': return v.v !== 0;
      case 'quant': return v.v.n !== 0n;
      case 'str': case 'word': return String(v.v).length > 0;
      case 'list': return v.items.length > 0;
      case 'record': return v.fields.length > 0;
      case 'range': {
        const number = (x) => (isNumber(x) ? asRat(x) : x.k === 'quant' ? x.v : null);
        const from = number(v.from), to = number(v.to);
        if (!from || !to) return true;
        const left = from.n * to.d, right = to.n * from.d;
        return v.inclusive ? left <= right : left < right;
      }
      case 'notes': return v.v.events.length > 0;
      case 'unit': return false;
      default: return true;
    }
  };

  const unary = (u, env) => {
    const v = evalNode(u.operand, env);
    let r;
    if (u.op === '!') r = { k: 'bool', v: !truthy(v) };
    else if (u.op === '-' && v.k === 'int') r = INT(-v.v);
    else if (u.op === '-' && v.k === 'float') r = FLOAT(-v.v, v.exact ? rat(-v.exact.n, v.exact.d) : null);
    else if (u.op === '-' && v.k === 'quant') r = { k: 'quant', v: rat(-v.v.n, v.v.d), dim: v.dim };
    else throw new Failure('type-mismatch', `${u.op} cannot be applied to ${describe(v)}`, u);
    return withSecret(withTaint(r, deepTainted(v)), deepSecret(v));
  };

  const isType = (v, type) => ({ Int: v.k === 'int', Float: v.k === 'float', Number: isNumber(v), Bool: v.k === 'bool', String: v.k === 'str',
      Length: v.k === 'quant' && v.dim === 'length', Time: v.k === 'quant' && v.dim === 'time',
      Duration: v.k === 'quant' && (v.dim === 'length' || v.dim === 'time'), Interval: v.k === 'quant' && v.dim === 'interval',
      Notes: v.k === 'notes', Record: v.k === 'record', Error: v.k === 'error',
          RequirementError: v.k === 'error' && v.type === 'RequirementError' }[type] || (v.k === 'error' && errorChain(v).some((t) => t.toLowerCase() === String(type).toLowerCase())) || false);
  const cmpResult = (op, c) => ({ k: 'bool', v: op === '==' ? c === 0 : op === '!=' ? c !== 0 : op === '<' ? c < 0 : op === '<=' ? c <= 0 : op === '>' ? c > 0 : c >= 0 });
  const ratCompare = (a, b) => { const x = a.n * b.d, y = b.n * a.d; return x < y ? -1 : x > y ? 1 : 0; };
  const arith = (op, l, r, at) => {
    const compare = ['==', '!=', '<', '<=', '>', '>='].includes(op);
    const zero = () => new Failure('divide-by-zero', 'Division by zero', at);
    if (l.k === 'int' && r.k === 'int') {
      if (compare) return cmpResult(op, l.v < r.v ? -1 : l.v > r.v ? 1 : 0);
      if (op === '+') return INT(l.v + r.v);
      if (op === '-') return INT(l.v - r.v);
      if (op === '*') return INT(l.v * r.v);
      if (op === '/') { if (r.v === 0n) throw zero(); return FLOAT(Number(l.v) / Number(r.v)); }
      if (op === '%') { if (r.v === 0n) throw zero(); return INT(l.v % r.v); }
    }
    if (isNumber(l) && isNumber(r)) {
      const x = asDouble(l), y = asDouble(r);
      if (compare) return cmpResult(op, x < y ? -1 : x > y ? 1 : 0);
      if (op === '+') return FLOAT(x + y);
      if (op === '-') return FLOAT(x - y);
      if (op === '*') return FLOAT(x * y);
      if (op === '/') { if (y === 0) throw zero(); return FLOAT(x / y); }
      if (op === '%') { if (y === 0) throw zero(); return FLOAT(x % y); }
    }
    if (l.k === 'quant' && r.k === 'quant' && l.dim === r.dim) {
      if (compare) return cmpResult(op, ratCompare(l.v, r.v));
      if (op === '+') return { k: 'quant', v: add(l.v, r.v), dim: l.dim };
      if (op === '-') return { k: 'quant', v: sub(l.v, r.v), dim: l.dim };
      if (op === '/') { if (r.v.n === 0n) throw zero(); return FLOAT(doubleOfRat(rat(l.v.n * r.v.d, l.v.d * r.v.n))); }
    }
    if (l.k === 'quant' && isNumber(r) && (op === '*' || op === '/')) {
      const k = asRat(r);
      if (op === '/' && k.n === 0n) throw zero();
      return { k: 'quant', v: op === '*' ? mul(l.v, k) : rat(l.v.n * k.d, l.v.d * k.n), dim: l.dim };
    }
    if (isNumber(l) && r.k === 'quant' && op === '*') return { k: 'quant', v: mul(asRat(l), r.v), dim: r.dim };
    if (l.k === 'str' && r.k === 'str' && compare) return cmpResult(op, l.v < r.v ? -1 : l.v > r.v ? 1 : 0);
    const joined = joinText(op, l, r, at);
    if (joined) return joined;
    if ((op === '==' || op === '!=') && ((l.k === 'bool' && r.k === 'bool') || ((l.k === 'word' || l.k === 'str') && (r.k === 'word' || r.k === 'str')))) {
      const equal = l.k === 'bool' ? l.v === r.v : l.v === r.v;
      return { k: 'bool', v: op === '==' ? equal : !equal };
    }
    throw new Failure('type-mismatch', `${op} cannot be applied to ${describe(l)} and ${describe(r)}`, at);
  };
  const binary = (b, env) => {
    if (b.op === '&&' || b.op === '||') {
      const left = evalNode(b.left, env), leftTruth = truthy(left);
      if (b.op === '&&' ? !leftTruth : leftTruth) return withTaint({ k: 'bool', v: leftTruth }, deepTainted(left));   // short circuit: the answer is always true or false
      const right = evalNode(b.right, env);
      return withTaint({ k: 'bool', v: truthy(right) }, deepTainted(left) || deepTainted(right));
    }
    if (b.op === 'is') { const subject = evalNode(b.left, env); return withTaint({ k: 'bool', v: isType(subject, b.right.kind === 'word' ? b.right.text : '') }, deepTainted(subject)); }
    const l = evalNode(b.left, env), r = evalNode(b.right, env);
    return withSecret(withTaint(arith(b.op, l, r, b), deepTainted(l) || deepTainted(r)), deepSecret(l) || deepSecret(r));
  };

  // ----- seeds
  const seedText = (n) => kit.seedText(n);
  const seedTree = (n, env) => {
    if (n.kind === 'call' && n.name === 'stable' && n.args.length === 1) {
      const name = evalNode(n.args[0], env);
      return { kind: 'call', name: 'stable', args: [{ kind: 'string', value: name.k === 'str' ? name.v : valText(name), start: n.args[0].start, end: n.args[0].end }], start: n.start, end: n.end, nameEnd: n.nameEnd };
    }
    if (n.kind === 'call' && n.name === 'random-seed') return n;
    if (n.kind === 'method' && n.name === 'fork') {
      let result = seedTree(n.target, env);
      for (const labelNode of n.args) {
        const label = evalNode(labelNode, env);
        const ln = label.k === 'str' ? { kind: 'string', value: label.v, start: labelNode.start, end: labelNode.end } : label.k === 'int' ? q(rat(label.v), null, labelNode, String(label.v)) : null;
        if (!ln && label.k === 'float') throw new Failure('seed-error', 'A fork label is a name in quotes or a whole number, and this is a Float (a division gives one): write Floor(...), Round(...) or Int(...) around it, or use %', labelNode, `Floor(${text.slice(labelNode.start, labelNode.end)})`);
        if (!ln) throw new Failure('seed-error', 'A fork label is a name in quotes or a whole number', labelNode);
        result = { kind: 'method', target: result, name: 'fork', args: [ln], start: n.start, end: n.end };
      }
      return result;
    }
    const v = evalNode(n, env);
    if (v.k === 'seed') return v.node;
    if (v.k === 'int' && v.v >= 0n) return q(rat(v.v), null, n, String(v.v));
    if (v.k === 'word') return { kind: 'word', text: v.v, start: n.start, end: n.end };
    throw new Failure('seed-error', 'A seed is a whole number, stable("name"), or one of those followed by .fork("label")', n);
  };
  const seed = (n, env) => { const node = seedTree(n, env); return { k: 'seed', node, text: seedText(node) }; };

  // ----- calls
  const near = (name) => {
    let best = null, bestD = 3;
    for (const candidate of [...kit.operatorNames, ...table.names(), ...LANG_FUNCTIONS]) {
      const d = kit.distance(name.toLowerCase(), candidate.toLowerCase());
      if (d < bestD) { best = candidate; bestD = d; }
    }
    return best;
  };
  const evalArgs = (c, env) => c.args.map((a) => (a.kind === 'named' ? { name: a.name, value: a.name === 'seed' ? seed(a.value, env) : evalNode(a.value, env), origin: a.value } : { name: null, value: evalNode(a, env), origin: a }));

  const volatileCall = (c, lower, env) => {
    if (inAssertion) throw new Failure('capability-denied', 'A clock, OS random or environment read is not available in an assertion', c);
    const capability = lower === 'osrandom' ? 'os-random' : lower === 'environment' ? 'environment' : 'clock';
    if (kit.capabilities && !kit.capabilities.allowed(capability)) throw new Failure('capability-denied', `Capability \`${capability}\` is ${kit.capabilities.why(capability)}`, { start: c.start, end: c.nameEnd });
    dynamic = true;
    let result;
    if (lower === 'currenttime' || lower === 'currentdate') { const t = (kit.clock ? kit.clock() : new Date()); const date = t instanceof Date ? t : new Date(t); const pad = (n, w = 2) => String(n).padStart(w, '0'); result = { k: 'now', t: date, iso: `${pad(date.getFullYear(), 4)}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}` }; }
    else if (lower === 'osrandom') result = INT(kit.osRandom ? kit.osRandom() : Math.floor(Math.random() * 2147483647));
    else {
      const args = evalArgs(c, env);
      if (args.length !== 1 || args[0].value.k !== 'str') throw new Failure('bad-argument', 'environment(name) takes the name of a variable in quotes', c);
      if (kit.capabilities?.nameAllowed && !kit.capabilities.nameAllowed('environment', args[0].value.v)) throw new Failure('capability-denied', `environment("${args[0].value.v}") is not allowed: this scope only allows ${(kit.capabilities.allowLists?.('environment') || []).map((l) => '[' + l.join(', ') + ']').join(' and ')}`, c);
      if (typeof kit.environment !== 'function') throw new Failure('needs-server', 'environment() is read by the server', c);
      const value = kit.environment(args[0].value.v) ?? '';
      const fingerprint = [...sha256(new TextEncoder().encode(value))].map((b) => b.toString(16).padStart(2, '0')).join('').slice(0, 8);
      dynamicInputs.push('environment("' + args[0].value.v + '") = sha256:' + fingerprint);   // the value itself can be a secret: only a fingerprint is recorded
      return { k: 'str', v: value, tainted: true, secret: true };
    }
    dynamicInputs.push(`${c.name}() = ${valText(result)}`);
    return { ...result, tainted: true };
  };

  const produced = (value, at) => { if (value.events.length > kit.limit) throw new Failure('cap-events', `More than ${kit.limit} notes`, at); return { k: 'notes', v: value }; };

  const argNode = (prm, value, env) => {
    if (prm && prm.type === 'RandomSource') return seed(value, env).node;
    if (value.kind === 'call' && ['bars', 'beats', 'time'].includes(value.name) && value.args.length === 1) return { ...value, args: [argNode(prm, value.args[0], env)] };
    const v = evalNode(value, env);
    argTaint = argTaint || deepTainted(v);
    const plain = nodeOf(v, value);
    return prm ? typedValue(prm, plain) : plain;
  };
  const catalogCall = (c, sig, env, piped) => {
    if (sig.receiver === 'Notes' && piped?.k !== 'notes') throw new Failure('bad-argument', `${c.name}(...) needs notes to work on: write notes |> ${c.name}(...)`, c);
    const nodes = [];
    let position = 0;
    const outerTaint = argTaint;
    argTaint = false;
    for (const a of c.args) {
      let prm = null, value = a, named = null;
      if (a.kind === 'named') { named = a.name; value = a.value; prm = sig.params.find((x) => x.name === a.name) ?? null; }
      else { prm = sig.params[position] ?? null; if (prm && prm.type !== 'Values') position++; }
      const resolved = argNode(prm, value, env);
      nodes.push(named ? { kind: 'named', name: named, value: resolved, start: a.start, end: a.end } : resolved);
    }
    const operandTaint = argTaint || !!piped?.tainted;
    argTaint = outerTaint;
    const call = { ...c, args: nodes };
    if (sig.receiver === null) { const generated = kit.generate(call); if (!generated) throw new Failure('reported', '', c); return withTaint(produced(generated, c), operandTaint); }
    const stepped = kit.step({ events: [...piped.v.events], span: piped.v.span }, call);
    if (!stepped) throw new Failure('reported', '', c);
    return withTaint(produced(stepped, c), operandTaint);
  };

  const toInt = (v, at, mode) => {
    if (v.k === 'int') return INT(v.v);
    if (v.k === 'float') {
      const r = v.exact ?? ratOfDouble(v.v);
      if (mode === 'exact') { if (!isInt(r)) throw new Failure('conversion', `${safeText(v)} is not a whole number: use Round, Floor, Ceil or Truncate`, at); return INT(r.n); }
      let whole = r.n / r.d, rest = r.n % r.d;
      if (rest < 0n) { whole -= 1n; rest += r.d; }
      return INT(mode === 'round' ? roundHalfEven(r) : mode === 'floor' ? whole : mode === 'ceil' ? (rest === 0n ? whole : whole + 1n) : (r.n < 0n && rest !== 0n ? whole + 1n : whole));
    }
    if (v.k === 'str' && mode === 'exact' && /^[+-]?\d+$/.test(v.v)) return INT(BigInt(v.v));
    throw new Failure('conversion', `${describe(v)} cannot be made a whole number`, at);
  };
  const minMax = (min, a, b, at) => {
    if (isNumber(a) && isNumber(b)) { const less = ratCompare(asRat(a), asRat(b)) < 0; return min === less ? a : b; }
    if (a.k === 'quant' && b.k === 'quant' && a.dim === b.dim) return min === (ratCompare(a.v, b.v) < 0) ? a : b;
    throw new Failure('type-mismatch', `${min ? 'min' : 'max'} needs two numbers or two quantities of the same kind`, at);
  };
  const makeNote = (c, args) => {
    let at = 0, len = BEAT, pitch = 60, vel = 100;
    const micro = (a, what) => { if (a.value.k !== 'quant') throw new Failure('type-mismatch', `${what} needs a length, like 1beat`, a.origin); return toMicro(a.value.v, 'beats', bpm) ?? 0; };
    for (const a of args) {
      if (a.name === 'at') at = micro(a, 'at');
      else if (a.name === 'len') len = Math.max(1, micro(a, 'len'));
      else if (a.name === 'pitch') pitch = Math.min(127, Math.max(0, Number(coerce(a.value, 'Pitch', a.origin, 'pitch').v)));
      else if (a.name === 'vel') vel = Math.min(127, Math.max(1, Number(coerce(a.value, 'Velocity', a.origin, 'vel').v)));
      else throw new Failure('bad-argument', 'Note takes at=, len=, pitch= and vel=', a.origin);
    }
    const events = [{ at, len, pitch, vel, tail: '', ext: '' }];
    return { k: 'notes', v: { events, span: Math.max(1, Math.ceil((at + len) / (4 * BEAT))) * 4 * BEAT } };
  };
  const builtin = (c, name, env, piped) => {
    const lower = name.toLowerCase();
    if (lower === 'random' || lower === 'choose' || lower === 'chance') {
      let anyTaint = false;
      const read = (x) => { const v = evalNode(x, env); anyTaint = anyTaint || deepTainted(v); return v; };
      const nodes = c.args.map((a) => (a.kind === 'named' ? { ...a, value: a.name === 'seed' ? seed(a.value, env).node : nodeOf(read(a.value), a.value) } : nodeOf(read(a), a)));
      const result = kit.valueCall({ ...c, name: lower, args: nodes });
      if (!result) throw new Failure('reported', '', c);
      return withTaint(fromNode(result), anyTaint);
    }
    if (lower === 'stable' || lower === 'random-seed') return seed(c, env);
    const args = evalArgs(c, env);
    if (piped) args.unshift({ name: null, value: piped, origin: c });
    const tainted = args.some((a) => deepTainted(a.value));
    const need = (i, what) => { if (i >= args.length) throw new Failure('bad-argument', `${name} needs ${what}`, c); return args[i].value; };
    let result = null;
    switch (lower) {
      case 'int': result = toInt(need(0, 'a value'), c, 'exact'); break;
      case 'float': { const v = need(0, 'a value'); if (v.k === 'int') result = FLOAT(Number(v.v), rat(v.v)); else if (v.k === 'float') result = v; else if (v.k === 'str' && /^[+-]?\d+(\.\d+)?$/.test(v.v)) result = FLOAT(Number(v.v)); else throw new Failure('conversion', `${describe(v)} cannot be made a Float`, c); break; }
      case 'round': case 'floor': case 'ceil': case 'truncate': result = toInt(need(0, 'a number'), c, lower); break;
      case 'count': case 'len': { const v = need(0, 'notes, a list or a string'); if (v.k === 'notes') result = INT(v.v.events.length); else if (v.k === 'list') result = INT(v.items.length); else if (v.k === 'str') result = INT(v.v.length); else throw new Failure('type-mismatch', `${name} needs notes, a list or a string, not ${describe(v)}`, c); break; }
      case 'span': { const v = need(0, 'notes'); if (v.k !== 'notes') throw new Failure('type-mismatch', 'span needs notes', c); result = { k: 'quant', v: rat(BigInt(v.v.span), BigInt(BEAT)), dim: 'length' }; break; }
      case 'abs': { const v = need(0, 'a number'); if (v.k === 'int') result = INT(v.v < 0n ? -v.v : v.v); else if (v.k === 'float') result = FLOAT(Math.abs(v.v), v.exact ? rat(v.exact.n < 0n ? -v.exact.n : v.exact.n, v.exact.d) : null); else if (v.k === 'quant') result = { k: 'quant', v: rat(v.v.n < 0n ? -v.v.n : v.v.n, v.v.d), dim: v.dim }; else throw new Failure('type-mismatch', `abs needs a number, not ${describe(v)}`, c); break; }
      case 'min': case 'max': result = minMax(lower === 'min', need(0, 'two values'), need(1, 'two values'), c); break;
      case 'at': { const list = need(0, 'a list'), index = need(1, 'an index'); if (list.k !== 'list' || index.k !== 'int' || index.v < 0n || index.v >= BigInt(list.items.length)) throw new Failure('bad-argument', 'at(list, index) needs a list and an index inside it', c); result = list.items[Number(index.v)]; break; }
      case 'note': result = makeNote(c, args); break;
      case 'string': if (args.length !== 1 || args[0].name !== null) throw new Failure('bad-argument', 'String takes exactly one value, like String(4)', c); result = toStringValue(args[0].value, args[0].origin); break;
      case 'sprintf': result = sprintf(args, c); break;
      case 'emptynotes': result = { k: 'notes', v: { events: [], span: 4 * BEAT } }; break;
      default: return null;
    }
    return withSecret(withTaint(result, tainted), args.some((a) => deepSecret(a.value)));
  };

  // ----- types
  let why = null, whyId = null, whySuggestion = null;
  const catalogType = (type) => NOTE_TYPES[type];
  const tryCoerce = (v, type, at) => {
    if (v.k === 'error' && errorChain(v).some((t) => t.toLowerCase() === String(type).toLowerCase())) return v;
    if (type.startsWith('List<') && type.endsWith('>')) {
      if (v.k !== 'list') return null;
      const items = [];
      for (const item of v.items) { const c = tryCoerce(item, type.slice(5, -1), at); if (!c) return null; items.push(withTaint(c, deepTainted(item))); }
      return { k: 'list', items };
    }
    if (type.startsWith('Range<') && type.endsWith('>')) { if (v.k !== 'range') return null; const from = tryCoerce(v.from, type.slice(6, -1), at), to = from && tryCoerce(v.to, type.slice(6, -1), at); return from && to ? { k: 'range', from, to, inclusive: v.inclusive } : null; }
    const members = table.type(type);
    if (members) { for (const m of members) { const r = tryCoerce(v, m, at); if (r) return r; } return null; }
    switch (type) {
      case 'Int': {
        if (v.k === 'int') return v;
        if (v.k === 'float') {
          const r = v.exact ?? ratOfDouble(v.v);
          if (isInt(r)) return INT(r.n);
          why = `${safeText(v)} is not a whole number: write Round, Floor, Ceil or Truncate to say how it becomes one`; whyId = 'lossy-conversion'; whySuggestion = `Round(${text.slice(at.start, at.end)})`;
          return null;
        }
        return null;
      }
      case 'Float': return v.k === 'float' ? v : v.k === 'int' ? FLOAT(Number(v.v), rat(v.v)) : null;
      case 'Number': return isNumber(v) ? v : null;
      case 'Bool': return v.k === 'bool' ? v : null;
      case 'String': case 'Text': return v.k === 'str' ? v : null;
      case 'Notes': return v.k === 'notes' ? v : null;
      case 'Record': return v.k === 'record' ? v : null;
      case 'Error': return v.k === 'error' ? v : null;
      case 'RequirementError': return v.k === 'error' && v.type === 'RequirementError' ? v : null;
      case 'Length': return v.k === 'quant' && v.dim === 'length' ? v : null;
      case 'Time': return v.k === 'quant' && v.dim === 'time' ? v : null;
      case 'Duration': return v.k === 'quant' && (v.dim === 'length' || v.dim === 'time') ? v : null;
      case 'Gain': return v.k === 'quant' && v.dim === 'gain' ? v : null;
      case 'Percent': return v.k === 'quant' && v.dim === 'percent' ? v : v.k === 'float' ? { k: 'quant', v: v.exact ?? ratOfDouble(v.v), dim: 'percent' } : null;
      case 'Interval': return v.k === 'quant' && v.dim === 'interval' ? v : isNumber(v) ? { k: 'quant', v: asRat(v), dim: 'interval' } : null;
      case 'RandomSource': case 'Seed': case 'StableSeed': case 'RandomStream': return v.k === 'seed' ? v : v.k === 'int' && v.v >= 0n ? { k: 'seed', node: q(rat(v.v), null, at, String(v.v)), text: String(v.v) } : null;
      case 'Values': return v.k === 'list' ? v : null;
      default: break;
    }
    if (type === 'Probability' && v.k === 'quant' && v.dim === 'percent') return FLOAT(doubleOfRat(v.v), v.v);
    const info = catalogType(type);
    if (!info) return null;
    if (info.kind === 'vocabulary') {
      const w = v.k === 'word' ? v.v : v.k === 'str' ? v.v : null;
      if (w === null) return null;
      const valid = info.name === 'PitchClass' || type === 'PitchClass' ? tryRoot(w) !== null : info.values.includes(w);
      if (!valid) return null;
      if (v.k === 'str') warn('quoted-word', `"${w}" is a quoted word where ${article(type)} is expected: write it without quotes`, at, w);
      return { k: 'word', v: w };
    }
    if (info.kind === 'vocabulary-list') return v.k === 'str' && v.v.split(' ').filter(Boolean).every((t) => tryChord(t)) ? v : null;
    if (info.kind === 'refined') {
      if (v.k === 'word' && info.noteNames) { const pitch = noteNameToPitch(v.v); if (pitch !== null) return INT(pitch, type); }
      if (info.base === 'Int') {
        if (v.k === 'float') { const r = v.exact ?? ratOfDouble(v.v); if (isInt(r)) v = INT(r.n); }
        if (v.k !== 'int') return null;
        if (v.brand && v.brand !== type) { why = `${article(v.brand)} is not ${article(type)}: both are whole numbers, but they mean different things (Int(...) or a calculation makes a plain number again)`; return null; }
        const out = INT(v.v, type);
        return (info.min == null || v.v >= BigInt(info.min)) && (info.max == null || v.v <= BigInt(info.max)) ? out : null;
      }
      if (!isNumber(v)) return null;
      const r = asRat(v);
      const inside = (info.min == null || r.n >= BigInt(info.min) * r.d) && (info.max == null || r.n <= BigInt(info.max) * r.d);
      return inside ? (v.k === 'int' ? FLOAT(Number(v.v), r) : v) : null;
    }
    return null;
  };
  const coerce = (v, typeText, at, what) => {
    const type = typeText.trim();
    why = whyId = whySuggestion = null;
    const r = tryCoerce(v, type, at);
    if (r) return withSecret(withTaint(r, v.tainted), deepSecret(v));
    const unit = ['Length', 'Time', 'Duration'].includes(type) && isNumber(v) ? ' (a number needs a unit: beat, bar, tick, ms or s)' : '';
    throw new Failure(unit ? 'missing-unit' : whyId ?? 'type-mismatch', `${what}: ${why ?? `${describe(v)} is not ${article(type)}`}${unit}`, at, whySuggestion);
  };

  // ----- helpers and statements
  const callHelper = (c, h, env, piped) => {
    if (inAssertion && h.effect === 'dynamic') throw new Failure('capability-denied', 'A dynamic helper is not available in an assertion', c);
    if (depth >= depthLimit) throw new Failure('stack-depth', `Helpers call each other more than ${depthLimit} deep (the limit of the song is limits.stack-depth)`, c);
    const { node, canonical } = h;
    const args = [];
    if (piped) args.push({ name: null, value: piped, origin: c });
    args.push(...evalArgs(c, env));
    const scope = newEnv();
    const bound = new Set();
    let position = 0;
    for (const a of args) {
      let prm;
      if (a.name !== null) { prm = node.params.find((x) => x.name === a.name); if (!prm) throw new Failure('bad-argument', `${canonical} has no parameter "${a.name}" (it has: ${node.params.map((x) => x.name).join(', ')})`, a.origin); }
      else { if (position >= node.params.length) throw new Failure('bad-argument', `${canonical} takes at most ${node.params.length} arguments`, a.origin); prm = node.params[position++]; }
      if (bound.has(prm.name)) throw new Failure('bad-argument', `"${prm.name}" is given twice`, a.origin);
      bound.add(prm.name);
      scope.vars.set(prm.name, { value: coerce(a.value, prm.type, a.origin, `${canonical}: ${prm.name}`), mutable: false });
    }
    for (const prm of node.params.filter((x) => !bound.has(x.name))) {
      if (!prm.default) throw new Failure('bad-argument', `${canonical} needs "${prm.name}" (${prm.type})`, c);
      scope.vars.set(prm.name, { value: coerce(evalNode(prm.default, newEnv()), prm.type, prm.default, `${canonical}: ${prm.name}`), mutable: false });
    }
    if (node.baked) {
      const baked = { ...evalNode(node.baked, newEnv()), tainted: false };
      bakedDynamic = true;
      return node.returns ? coerce(baked, node.returns, node.baked, canonical) : baked;
    }
    depth++;
    try {
      const frame = { returned: false, returnValue: null, emitted: null, last: null };
      execBlock(node.body, scope, frame);
      const result = frame.returned ? frame.returnValue ?? frame.emitted : frame.emitted ?? frame.last;
      if (!result || result.k === 'unit') throw new Failure('bad-argument', `"${canonical}" gave no value: end it with an expression, emit or return`, node.body);
      account(result, node.body);
      const out = h.effect === 'dynamic' ? withTaint(result, true) : result;   // a dynamic helper may read a volatile source: whatever it returns is tainted
      return node.returns ? coerce(out, node.returns, node.body, canonical) : out;
    } finally { depth--; }
  };
  const truth = (cond, env) => { const v = evalNode(cond, env); decisionTainted = deepTainted(v); return truthy(v); };
  const whole = (n, env, what) => { const v = evalNode(n, env); wholeTainted = wholeTainted || deepTainted(v); return coerce(v, 'Int', n, what).v; };
  /** Runs one evaluation in the assertion fork: its own steps, depth and error lists; whatever it does to the state of the run is undone (plan D27). */
  const assertionFork = (evaluate) => {
    const available = Math.min(ASSERTION_STATEMENT_FUEL, ASSERTION_SESSION_FUEL - assertionSpent);
    if (available <= 0) return { kind: 'unavailable' };
    const saved = {
      fuel, fuelLimit, depth, depthLimit, noteBytes, memoryLimitMb, dynamic, bakedDynamic, control, decisionTainted, wholeTainted, argTaint, reserveExhausted,
      cached: new Set(constantCache.keys()), busy: new Set(busy),
      inputs: dynamicInputs.length, errors: errors.length, warnings: warnings.length, handling: handling.length,
    };
    fuel = 0; fuelLimit = available; depthLimit = depth + ASSERTION_DEPTH; inAssertion = true;
    noteBytes = 0; memoryLimitMb = ASSERTION_MEMORY_MB;   // its own memory budget: neither the notes of the main run count against it nor it against them
    try { return { kind: 'value', value: evaluate() }; } catch (e) {
      if (!(e instanceof Failure)) throw e;
      return { kind: ASSERTION_UNAVAILABLE.has(e.id) ? 'unavailable' : 'raised' };
    } finally {
      assertionSpent += Math.min(available, fuel);
      inAssertion = false;
      ({ fuel, fuelLimit, depth, depthLimit, noteBytes, memoryLimitMb, dynamic, bakedDynamic, control, decisionTainted, wholeTainted, argTaint, reserveExhausted } = saved);
      // a constant that the fork evaluated is evaluated again by the main run: its dependency and its dynamic flag stay the main run's
      for (const key of [...constantCache.keys()]) if (!saved.cached.has(key)) constantCache.delete(key);
      busy.clear(); for (const key of saved.busy) busy.add(key);
      dynamicInputs.length = saved.inputs; errors.length = saved.errors; warnings.length = saved.warnings; handling.length = saved.handling;
    }
  };
  /** assert (plan D9r, D27): off, neither the condition nor the message runs (the tick of the statement was charged either way); on, a failure is fatal (T2) and never catchable. */
  const assertStatement = (s, env) => {
    if (inAssertion || !kit.assertions) return;
    const condition = s.positional[0];
    const text = s.conditionText ?? '';
    const outcome = assertionFork(() => truthy(evalNode(condition, env)));
    if (outcome.kind === 'value' && outcome.value) return;
    if (outcome.kind === 'unavailable') throw new Failure('assert-unavailable', 'The assertion could not be evaluated within its budget: ' + text, s);
    if (outcome.kind === 'raised') throw new Failure('assert', 'Assertion failed: the condition raised an error (' + text + ')', s);
    let message = null;
    if (s.positional.length > 1) {
      const made = assertionFork(() => { const v = evalNode(s.positional[1], env); return v.k === 'str' ? v.v : valText(v); });
      if (made.kind === 'value') message = made.value;
    }
    throw new Failure('assert', message ?? 'Assertion failed: ' + text, s);
  };

  const isCatchable = (e) => e instanceof Failure && e.id in CATCHABLE_CODES;
  const severity = (e) => (e instanceof Failure && SAFETY_STOPS.has(e.id) ? 3 : isCatchable(e) ? 1 : 2);
  const errorOf = (failure) => {
    const base = failure.error ?? { k: 'error', type: failure.id === 'require' ? 'RequirementError' : 'Error',
        chain: failure.id === 'require' ? ['RequirementError', 'Error'] : ['Error'], message: failure.message,
        code: CATCHABLE_CODES[failure.id] ?? null, hint: failure.suggestion ?? null, context: null, cause: null, origin: 'error' };
    return { ...base, suppressed: (failure.suppressed ?? []).slice(0, 4), suppressedCount: failure.suppressedTotal ?? 0, ...(failure.tainted ? { tainted: true } : {}) };
  };
  const errorField = (error, name, at) => {
    let result;
    switch (name) {
      case 'type': result = { k: 'str', v: error.type }; break;
      case 'message': result = { k: 'str', v: error.message }; break;
      case 'code': result = { k: 'str', v: error.code ?? '' }; break;
      case 'origin': result = { k: 'str', v: error.origin }; break;
      case 'context': result = error.context ?? { k: 'record', fields: [] }; break;
      case 'cause': if (!error.cause) throw new Failure('unknown-field', 'This error has no cause', at); result = error.cause; break;
      case 'suppressed': result = { k: 'list', items: (error.suppressed ?? []).map((s) => ({ k: 'record', fields: [['type', { k: 'str', v: s.type }],
          ['message', { k: 'str', v: s.message }], ['code', { k: 'str', v: s.code ?? '' }]] })) }; break;
      case 'suppressedCount': result = INT(error.suppressedCount ?? 0); break;
      default: {
        const hit = (error.fields ?? []).find(([key]) => key === name);
        if (!hit) throw new Failure('unknown-field', `An error has no field "${name}" (it has type, message, code, origin, context, cause, suppressed, suppressedCount)`, at);
        result = hit[1];
      }
    }
    return withTaint(result, error.tainted);
  };
  const errorChain = (v) => v.chain ?? ['Error'];
  const constructError = (c, env) => {
    const spec = table.errorSpec(c.name);
    if (!spec) throw new Failure('unknown-operator', `Unknown error "${c.name}"`, { start: c.start, end: c.nameEnd });
    const name = spec.chain[0];
    let message = null, code = null, hint = null, context = null, cause = null, tainted = control > 0;
    const given = new Map();
    for (const argument of c.args) {
      const named = argument.kind === 'named';
      const value = evalNode(named ? argument.value : argument, env);
      tainted = tainted || deepTainted(value);
      if (!named && message === null && value.k === 'str') message = value.v;
      else if (!named) throw new Failure('type-mismatch', `${name} takes one message (a String) first; everything else is named`, argument);
      else if (argument.name === 'code' && value.k === 'str') code = value.v;
      else if (argument.name === 'hint' && value.k === 'str') hint = value.v;
      else if (argument.name === 'context' && value.k === 'record') context = value;
      else if (argument.name === 'cause' && value.k === 'error') cause = value;
      else if (spec.fields.some((f) => f.name === argument.name)) { if (given.has(argument.name)) throw new Failure('bad-argument',
          `"${argument.name}" is given twice`, argument); given.set(argument.name, value); }
      else throw new Failure('bad-argument', `${name} has no argument "${argument.name}" of this kind (message, code=String, hint=String,
          context=Record, cause=Error${spec.fields.map((f) => ', ' + f.name + '=' + f.type).join('')})`, argument);
    }
    if (message === null) throw new Failure('bad-argument', `${name} needs a message`, c);
    const fields = spec.fields.map((f) => {
      if (!given.has(f.name)) throw new Failure('bad-argument', `${name} needs the field "${f.name}" (${f.type})`, c);
      return [f.name, coerce(given.get(f.name), f.type, c, `${name}.${f.name}`)];
    });
    return { k: 'error', type: name, chain: spec.chain, message, code, hint, context,
        cause: cause ?? (handling.length ? handling[handling.length - 1] : null), origin: 'error', suppressed: [], suppressedCount: 0, fields, ...(tainted ? { tainted: true } : {}) };
  };
  const throwStatement = (s, env) => {
    const value = evalNode(s.positional[0], env);
    let error;
    if (value.k === 'error') error = value;
    else if (value.k === 'str') error = { k: 'error', type: 'Error', message: value.v, code: null, hint: null, context: null,
        cause: handling.length ? handling[handling.length - 1] : null, origin: 'string', suppressed: [], suppressedCount: 0, ...(value.tainted ? { tainted: true } : {}) };
    else throw new Failure('type-mismatch', `throw needs a message or an Error, not ${describe(value)}`, s.positional[0]);
    const failure = new Failure('throw', error.message, s, error.hint);
    failure.error = error;
    if (control > 0 || deepTainted(error)) failure.tainted = true;
    throw failure;
  };
  /** A failure arrived while another was in flight: strictly more severe replaces the primary (which is kept as displaced), otherwise it is attached as suppressed (at most 4 summaries). */
  const replace = (primary, arrived) => {
    if (!primary) return arrived;
    if (severity(arrived) > severity(primary)) { if (arrived instanceof Failure && primary instanceof Failure) (arrived.displaced ??= []).push(primary); return arrived; }
    if (primary instanceof Failure && arrived instanceof Failure) {
      primary.suppressedTotal = (primary.suppressedTotal ?? 0) + 1;
      (primary.suppressed ??= []);
      if (primary.suppressed.length < 4) primary.suppressed.push({ type: arrived.error?.type ?? 'Error', message: arrived.message,
          code: arrived.error?.code ?? CATCHABLE_CODES[arrived.id] ?? null });
    }
    return primary;
  };
  const runFinally = (block, env, frame, primary) => {
    const reserve = !!primary && severity(primary) === 2 && !inReserve;   // a finally inside a finally that already runs on the reserve shares it
    const saved = { fuel, fuelLimit, depthLimit };
    // earlier finally blocks of this unwinding already spent part of the reserve
    if (reserve) { fuel = primary.reserveUsed ?? 0; fuelLimit = UNWIND_FUEL; depthLimit = depth + UNWIND_DEPTH; inReserve = true; }
    try { execBlock(block, newEnv(env), frame); return primary; } catch (e) {
      if (!(e instanceof Failure)) throw e;
      if ((reserve || inReserve) && (e.id === 'fuel' || e.id === 'stack-depth')) { const first = !reserveExhausted; reserveExhausted = true; if (first) warn('unwind-reserve',
          'A finally block ran out of its small reserve while the evaluation was stopping: the remaining finally blocks were skipped', block); return primary; }
      return replace(primary, e);
    } finally { if (reserve) { primary.reserveUsed = fuel; inReserve = false; ({ fuel, fuelLimit, depthLimit } = saved); } }
  };
  const tryStatement = (s, env, frame) => {
    const emittedBefore = frame.emitted;
    let flow = 'next';
    let pending = null;
    try { flow = execBlock(s.body, newEnv(env), frame); } catch (e) { if (!(e instanceof Failure)) throw e; pending = e; }
    if (pending && isCatchable(pending)) {
      const error = errorOf(pending);
      const clause = s.catches.find((c) => !c.type || errorChain(error).some((t) => t.toLowerCase() === c.type.toLowerCase()));
      if (clause) {
        frame.emitted = emittedBefore;   // what the try body emitted is dropped: the catch decides what happens next
        pending = null;
        const scope = newEnv(env);
        scope.vars.set(clause.name, { value: error, mutable: false });
        handling.push(error);
        try { flow = selected(clause.body, scope, frame, !!error.tainted); } catch (e) { if (!(e instanceof Failure)) throw e; pending = e; } finally { handling.pop(); }
      }
    }
    if (s.finally && pending && severity(pending) === 3) throw pending;
    if (s.finally && !reserveExhausted) pending = runFinally(s.finally, env, frame, pending);
    if (pending) throw pending;
    return flow;
  };

  const execBlock = (block, env, frame) => { for (const s of block.statements) if (exec(s, env, frame) === 'return') return 'return'; return 'next'; };
  const mergeNotes = (a, b, at) => {
    const merged = kit.step({ events: [...a.v.events], span: a.v.span }, { kind: 'call', name: 'merge', args: [{ kind: 'value', value: b.v, start: at.start, end: at.end }], start: at.start, end: at.end, nameEnd: at.end });
    if (!merged) throw new Failure('reported', '', at);
    return withTaint(produced(merged, at), a.tainted || b.tainted);
  };
  const exec = (s, env, frame) => {
    tick(s);
    switch (s.kind) {
      case 'let': {
        if (env.vars.has(s.name)) throw new Failure('duplicate-variable', `"${s.name}" is already defined here`, s);
        const v = evalNode(s.value, env);
        env.vars.set(s.name, { value: ctl(s.type ? coerce(v, s.type, s.value, s.name) : v), mutable: s.mutable });
        return 'next';
      }
      case 'assign': {
        const v = evalNode(s.value, env);
        let e = env;
        while (e && !e.vars.has(s.name)) e = e.parent;
        if (!e) throw new Failure('undefined-variable', `There is no variable "${s.name}"`, s);
        if (!e.vars.get(s.name).mutable) throw new Failure('immutable-variable', `"${s.name}" was made with let: use var to change it`, s);
        e.vars.set(s.name, { value: ctl(v), mutable: true });
        return 'next';
      }
      case 'if': {
        let chosenByTaint = false;   // a branch that runs because earlier conditions were false is chosen by them too
        for (const b of s.branches) { const taken = truth(b.cond, env); chosenByTaint = chosenByTaint || decisionTainted; if (taken) return selected(b.body, newEnv(env), frame, chosenByTaint); }
        return s.otherwise ? selected(s.otherwise, newEnv(env), frame, chosenByTaint) : 'next';
      }
      case 'try': return tryStatement(s, env, frame);
      case 'for': return runFor(s, env, frame);
      case 'repeat': {
        wholeTainted = false;
        const count = whole(s.count, env, 'repeat');
        const repeatTainted = wholeTainted;
        if (count > BigInt(limits.loopIterations)) throw new Failure('loop-limit', `This loop would run ${count} times; the limit is ${limits.loopIterations} (limits.loop-iterations)`, s.count);
        for (let k = 0n; k < count; k++) { tick(s); if (selected(s.body, newEnv(env), frame, repeatTainted) === 'return') return 'return'; }
        return 'next';
      }
      case 'while': {
        const max = s.max ? whole(s.max, env, 'max') : BigInt(limits.whileDefault);
        if (max > BigInt(limits.loopIterations)) throw new Failure('loop-limit', `max ${max} is above the limit of ${limits.loopIterations} (limits.loop-iterations)`, s.max ?? s);
        let n = 0n;
        while (truth(s.cond, env)) {
          const loopTainted = decisionTainted;
          tick(s);
          if (n >= max) { if (s.stop) break; throw new Failure('while-limit', `The while loop reached its max of ${max} and its condition is still true (write max ${max} stop if stopping there is what you want)`, s); }
          if (selected(s.body, newEnv(env), frame, loopTainted) === 'return') return 'return';
          n++;
        }
        return 'next';
      }
      case 'return': frame.returned = true; frame.returnValue = s.value ? ctl(evalNode(s.value, env)) : null; return 'return';
      case 'emit': {
        const v = evalNode(s.value, env);
        if (v.k !== 'notes') throw new Failure('type-mismatch', `emit needs Notes, not ${describe(v)}`, s.value);
        const emitted = ctl(v);
        frame.emitted = frame.emitted ? mergeNotes(frame.emitted, emitted, s) : emitted;
        return 'next';
      }
      case 'exprstmt': frame.last = ctl(evalNode(s.value, env)); return 'next';
      case 'observe':
        if (SEMANTIC_STATEMENTS.has(s.name)) raise(s, env);
        else if (s.name === 'assert') assertStatement(s, env);
        return 'next';
      default: throw new Failure('bad-argument', 'This is not a statement', s);
    }
  };
  const runFor = (f, env, frame) => {
    if (f.to) {
      wholeTainted = false;
      const from = whole(f.from, env, 'the start of the range'), to = whole(f.to, env, 'the end of the range') + (f.inclusive ? 1n : 0n);
      const boundsTainted = wholeTainted;
      if (to - from > BigInt(limits.loopIterations)) throw new Failure('loop-limit', `This loop would run ${to - from} times; the limit is ${limits.loopIterations} (limits.loop-iterations)`, f.from);
      for (let i = from; i < to; i++) {
        tick(f);
        const scope = newEnv(env);
        scope.vars.set(f.var, { value: withTaint(INT(i), boundsTainted), mutable: false });
        if (selected(f.body, scope, frame, boundsTainted) === 'return') return 'return';
      }
      return 'next';
    }
    const iterable = evalNode(f.from, env);
    let items;
    if (iterable.k === 'list') items = iterable.items;
    else if (iterable.k === 'notes') items = iterable.v.events.map((n) => ({ k: 'note', n }));
    else if (iterable.k === 'range' && iterable.from.k === 'int' && iterable.to.k === 'int') { items = []; const end = iterable.to.v + (iterable.inclusive ? 1n : 0n); for (let i = iterable.from.v; i < end && items.length <= limits.loopIterations; i++) items.push(INT(i)); }
    else throw new Failure('type-mismatch', `A for loop goes over a range, a list or notes, not ${describe(iterable)}`, f.from);
    let taken = 0;
    for (const item of items) {
      if (++taken > limits.loopIterations) throw new Failure('loop-limit', `This loop would run more than ${limits.loopIterations} times (limits.loop-iterations)`, f.from);
      tick(f);
      const scope = newEnv(env);
      const rangeTainted = iterable.k === 'range' && deepTainted(iterable);
      scope.vars.set(f.var, { value: withTaint(item, !!iterable.tainted || rangeTainted), mutable: false });
      if (selected(f.body, scope, frame, rangeTainted) === 'return') return 'return';
    }
    return 'next';
  };

  const call = (c, env, piped) => {
    const lower = c.name.toLowerCase();
    if (VOLATILE.includes(lower)) return volatileCall(c, lower, env);
    const h = table.helper(c.name);
    if (h) {
      if (h.canonical !== c.name) warn('helper-casing', `Helper "${h.canonical}" found, but the casing differs`, { start: c.start, end: c.nameEnd }, h.canonical);
      return callHelper(c, h, env, piped);
    }
    const sig = NOTE_SIGNATURES[c.name];
    if (sig && !['random', 'choose', 'chance', 'stable', 'random-seed', 'bake'].includes(c.name)) return catalogCall(c, sig, env, piped);
    if (table.isErrorType?.(c.name)) return constructError(c, env);
    const built = builtin(c, c.name, env, piped);
    if (built) return built;
    if (table.isBroken?.(c.name)) throw new Failure('helper-broken', `The helper "${c.name}" is not available: its declaration has an error (fix that first)`, { start: c.start, end: c.nameEnd });
    throw new Failure('unknown-operator', `Unknown operator "${c.name}"`, { start: c.start, end: c.nameEnd }, near(c.name));
  };

  const pipelineVal = (p, env, top) => {
    const first = evalNode(p.source, env);
    if (first.k !== 'notes') throw new Failure('type-mismatch', `A pipeline starts with notes, not with ${describe(first)}`, p.source);
    let current = first;
    let from = 0;
    p.steps.forEach((s, i) => { if (s.kind === 'bake' && s.stage.hasBody && (s.stage.notes !== null || s.stage.file)) from = i + 1; });
    if (from > 0) {
      const stored = p.steps[from - 1];
      if (stored.stage.notes === null) throw new Failure('needs-server', `The bake stage "${stored.stage.name}" keeps its notes in a file`, stored);
      const stage = kit.storedNotes(stored);
      if (!stage) throw new Failure('reported', '', stored);
      current = { k: 'notes', v: stage };
    }
    for (let i = from; i < p.steps.length; i++) {
      const step = p.steps[i];
      if (step.kind === 'bake') { if (!top) throw new Failure('bad-argument', 'A note bake stage belongs to the pipeline of a clip, not to a helper', step); continue; }
      if (step.kind !== 'call') throw new Failure('bad-argument', 'A pipeline step is an operator or helper call', step);
      const next = call(step, env, current);
      if (next.k !== 'notes') throw new Failure('type-mismatch', `${step.name} gave ${typeName(next)}, not Notes`, step);
      current = withTaint(next, current.tainted);
    }
    return current;
  };

  const bakeValue = (b, env) => {
    const baked = { ...evalNode(b.value, env), tainted: false };
    if (baked.k === 'notes') throw new Failure('type-mismatch', 'A value bake keeps a value (a number, a length, a word, a string); notes are baked with |> bake name { ... }', b.value);
    if (isVolatileNode(b.expr, table)) { bakedDynamic = true; return baked; }   // a volatile expression is not run: the baked value is the value
    const current = evalNode(b.expr, env);
    if (deepTainted(current) || control > 0) {
      const why = `${valText(current)} depends on a volatile value that is read before this bake: baking it keeps the value, but the evaluation stays dynamic. `;
      warn('bake-upstream-dynamic', why + 'Bake the call that reads it instead', { start: b.asStart ?? b.start, end: b.end });
    }
    return baked;
  };

  function evalNode(node, env) {
    tick(node);
    switch (node.kind) {
      case 'quantity': return fromQuantity(node);
      case 'string': return { k: 'str', v: node.value };
      case 'bool': return { k: 'bool', v: node.value };
      case 'word': return word(node, env);
      case 'list': return { k: 'list', items: node.items.map((i) => evalNode(i, env)), ...(control > 0 ? { tainted: true } : {}) };
      case 'record': return { k: 'record', fields: node.fields.map((f) => [f.name, evalNode(f.value, env)]), ...(control > 0 ? { tainted: true } : {}) };
      case 'range': return { k: 'range', from: fromQuantity(node.from), to: fromQuantity(node.to), inclusive: node.inclusive };
      case 'rangeexpr': return { k: 'range', from: evalNode(node.from, env), to: evalNode(node.to, env), inclusive: node.inclusive };
      case 'reference': {
        const found = node.escaped ? null : lookup(env, node.block);
        if (found) return field(found.value, node.port, node);
        const v = kit.evalNode(node);
        if (!v) throw new Failure('reported', '', node);
        return { k: 'notes', v };
      }
      case 'literal': case 'value': { const v = kit.evalNode(node); if (!v) throw new Failure('reported', '', node); return { k: 'notes', v }; }
      case 'unary': return unary(node, env);
      case 'binary': return binary(node, env);
      case 'ternary': { const condition = evalNode(node.cond, env); return withTaint(evalNode(truthy(condition) ? node.then : node.otherwise, env), deepTainted(condition)); }
      case 'field': return field(evalNode(node.target, env), node.name, node);
      case 'method': {
        if (node.name === 'fork') return seed(node, env);
        if (node.name === 'next' && node.target.kind === 'word' && kit.streams && node.target.text in kit.streams) {
          if (inAssertion) throw new Failure('capability-denied', 'A random stream cannot be read in an assertion: reading it moves it on', node);
          const r = kit.streamNext(node.target, node); if (!r) throw new Failure('reported', '', node); return fromNode(r); }
        const builtin = { floor: 'Floor', ceil: 'Ceil', ceiling: 'Ceil', round: 'Round', truncate: 'Truncate', trunc: 'Truncate', abs: 'abs', int: 'Int', toint: 'Int', float: 'Float', tofloat: 'Float', min: 'min', max: 'max', count: 'count', length: 'count', len: 'count' }[node.name.toLowerCase()];
        const target = text.slice(node.target.start, node.target.end);
        throw new Failure('bad-argument', builtin ? `Numbers have no method "${node.name}": the built-in is written ${builtin}(value), like ${builtin}(${target})` : `Unknown method "${node.name}" (the methods are .fork(label) on a seed and .next() on a random-stream)`, node, builtin ? `${builtin}(${target})` : null);
      }
      case 'call': return call(node, env, null);
      case 'pipeline': { const made = pipelineVal(node, env, true); account(made, node); return made; }
      case 'bakevalue': return bakeValue(node, env);
      case 'named': return evalNode(node.value, env);
      default: throw new Failure('bad-argument', 'This cannot be evaluated here', node);
    }
  }

  // ----- the run
  const result = { value: null, dynamic: false, tainted: false, bakedDynamic: false, dynamicInputs, fuel: 0 };
  try {
    const v = evalNode(ast, newEnv());
    if (v.k !== 'notes') throw new Failure('type-mismatch', `The notes of a clip must be Notes, this expression is ${typeName(v)}`, ast);
    result.value = v.v;
    result.tainted = deepTainted(v);
  } catch (e) {
    if (!(e instanceof Failure)) throw e;
    if (e.id !== 'reported') errors.push({ id: e.id, message: e.message, start: e.node?.start ?? 0, end: e.node?.end ?? text.length, ...(e.suggestion ? { suggestion: e.suggestion } : {}) });
  }
  result.dynamic = dynamic;
  result.bakedDynamic = bakedDynamic;
  result.fuel = fuel;
  return result;
}

/** The helper, type and constant declarations of a song text, in the order they stand: [{ text, line }] (a declaration starts at the first column and runs while its braces and parentheses are open, or the next line continues the pipeline). */
export function helperSourcesOf(songText) {
  const lines = blankBlocks(String(songText).replace(/\r\n/g, '\n')).text.split('\n');   // a block comment is blanks, like the parser of the server reads it
  const start = /^(?:dynamic\s+helper|helper)\s+\S|^type\s+[A-Za-z][\w-]*\s*=|^error\s+[A-Za-z]|^let\s+\S/;
  const depthOf = (line, depth) => {
    let inString = false;
    for (let k = 0; k < line.length; k++) {
      const c = line[k];
      if (c === '"') inString = !inString;
      else if (!inString && c === '#' && !(k > 0 && /[A-Za-z0-9]/.test(line[k - 1]))) break;
      else if (!inString && (c === '{' || c === '(')) depth++;
      else if (!inString && (c === '}' || c === ')')) depth = Math.max(0, depth - 1);
    }
    return depth;
  };
  const found = [];
  for (let i = 0; i < lines.length; i++) {
    if (!start.test(lines[i])) continue;
    const first = i;
    let text = lines[i], depth = depthOf(lines[i], 0);
    while (i + 1 < lines.length && (depth > 0 || lines[i + 1].trimStart().startsWith('|>'))) { i++; text += '\n' + lines[i]; depth = depthOf(lines[i], depth); }
    found.push({ text, line: first });
  }
  return found;
}
