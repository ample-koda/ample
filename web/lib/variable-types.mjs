// The variables of the helpers of a song (let, var, constants, parameters, loop variables) and the type of each, worked out from the values without running anything: the browser twin of
// NoteLang.Variables.cs and of VariableHover in Queries.cs (docs/language/18-comments-and-doc-comments.md). @ample:variable-types

import { NOTE_BUILTINS, NOTE_SIGNATURES, NOTE_TYPES } from './note-catalog.mjs';

const QUANTITIES = ['Length', 'Time', 'Interval', 'Gain', 'Percent'];
const isNumber = (t) => t === 'Int' || t === 'Float';
const shorten = (text) => { const one = String(text).replace(/\s+/g, ' ').trim(); return one.length > 90 ? one.slice(0, 90) + ' …' : one; };

function combine(op, a, b) {
  if (op === '+' && a === 'String' && b === 'String') return 'String';
  if (isNumber(a) && isNumber(b)) return op === '/' ? 'Float' : a === 'Float' || b === 'Float' ? 'Float' : 'Int';
  const qa = QUANTITIES.includes(a), qb = QUANTITIES.includes(b);
  if (qa && isNumber(b) && (op === '*' || op === '/')) return a;
  if (isNumber(a) && qb && op === '*') return b;
  if (qa && qb) {
    if (op === '/' && a === b) return 'Float';
    if (op === '+' || op === '-' || op === '%') return a === b ? a : (a === 'Length' || a === 'Time') && (b === 'Length' || b === 'Time') ? 'Duration' : null;
  }
  return null;
}

const elementOf = (type) => { const m = /^(?:List|Range)<(.+)>$/.exec(type); return m ? m[1] : type === 'Notes' ? 'Note' : null; };
const unitType = (q) => (!q.unit ? (/^\d+$/.test(q.text ?? '') ? 'Int' : 'Float') : ['bar', 'bars', 'beat', 'beats', 'tick', 'ticks'].includes(q.unit) ? 'Length' : ['s', 'ms'].includes(q.unit) ? 'Time' : ['st', 'oct', 'cents'].includes(q.unit) ? 'Interval' : ['dB', 'db'].includes(q.unit) ? 'Gain' : q.unit === '%' ? 'Percent' : 'Number');

function* descendants(node) {
  if (!node || typeof node !== 'object') return;
  for (const value of Object.values(node)) {
    if (Array.isArray(value)) { for (const item of value) { if (item && typeof item === 'object' && 'kind' in item) { yield item; yield* descendants(item); } else if (Array.isArray(item)) for (const sub of item) if (sub && typeof sub === 'object') { if ('kind' in sub) yield sub; yield* descendants(sub); } } }
    else if (value && typeof value === 'object' && 'kind' in value) { yield value; yield* descendants(value); }
  }
}

/** The places where variables are declared and used: [{ line, start, end, keyword, detail: { name, kind, type, potential, origin, valueText } }] (offsets from the start of the declaration at that line). `table` is a helper table of note-lang.mjs. */
export function variableSpots(table) {
  const decls = table.all();
  const constants = new Map(decls.filter((d) => d.node.kind === 'constdecl').map((d) => [d.name.toLowerCase(), d]));
  const helpers = new Map(decls.filter((d) => d.node.kind === 'helper').map((d) => [d.name.toLowerCase(), d]));
  const typeAliases = decls.filter((d) => d.node.kind === 'typedecl').map((d) => d.node);
  const constantInfos = new Map(), busy = new Set(), spots = [], pending = [], inferred = new Map();

  const detailOf = (v) => ({ name: v.name, kind: v.kind, type: v.declared ?? (v.types.length ? v.types.join(' | ') : 'unknown'), potential: !v.declared && v.types.length > 1 ? [...v.types] : [], origin: v.declared ? 'the declared type' : v.origin, valueText: v.valueText });
  const typesOf = (v) => (v.declared ? [v.declared] : v.types);

  function constantInfo(name) {
    const key = name.toLowerCase();
    if (constantInfos.has(key)) return constantInfos.get(key);
    const d = constants.get(key), info = { name: d.name, kind: 'const', types: [], declared: null, origin: '', valueText: shorten(d.text.slice(d.node.value.start, d.node.value.end)) };
    constantInfos.set(key, info);
    if (d.node.type) info.declared = d.node.type;
    else if (!busy.has(key)) { busy.add(key); const r = infer(d.node.value, []); info.types.push(...r.types); info.origin = r.origin; busy.delete(key); }
    return info;
  }

  function infer(n, scopes) {
    const found = [];
    const add = (t) => { if (t && !found.includes(t)) found.push(t); };
    const find = (name) => { for (let i = scopes.length - 1; i >= 0; i--) if (scopes[i].has(name)) return scopes[i].get(name); return null; };
    let origin = '';
    if (!n) return { types: found, origin };
    switch (n.kind) {
      case 'literal': add('Notes'); origin = n.editable ? 'editable notes' : 'a list of notes'; break;
      case 'reference': add('Notes'); origin = 'the notes of another block'; break;
      case 'quantity': add(unitType(n)); origin = n.unit ? `a number with the unit ${n.unit}` : 'a number'; break;
      case 'string': add('String'); origin = 'a string'; break;
      case 'bool': add('Bool'); origin = 'true or false'; break;
      case 'list': { const items = []; for (const item of n.items) for (const t of infer(item, scopes).types) if (!items.includes(t)) items.push(t); add(items.length ? `List<${items.join(' | ')}>` : 'List'); origin = 'a list'; break; }
      case 'rangeexpr': { const ends = [...new Set([...infer(n.from, scopes).types, ...infer(n.to, scopes).types])]; add(ends.length ? `Range<${ends.join(' | ')}>` : 'Range'); origin = 'a range'; break; }
      case 'range': add('Range<' + (n.from.unit ? unitType(n.from) : 'Int') + '>'); origin = 'a range'; break;
      case 'named': return infer(n.value, scopes);
      case 'ternary': { infer(n.then, scopes).types.forEach(add); infer(n.otherwise, scopes).types.forEach(add); origin = 'a conditional: the types of both branches'; break; }
      case 'bakevalue': return infer(n.expr, scopes);
      case 'unary': if (n.op === '!') { add('Bool'); origin = 'a negation'; } else { infer(n.operand, scopes).types.forEach(add); origin = 'a signed value'; } break;
      case 'binary':
        if (['==', '!=', '<', '>', '<=', '>=', '&&', '||', 'is', 'and', 'or'].includes(n.op)) { add('Bool'); origin = 'a comparison'; break; }
        for (const x of infer(n.left, scopes).types) for (const y of infer(n.right, scopes).types) add(combine(n.op, x, y));
        origin = 'arithmetic on the types of both sides';
        break;
      case 'word': {
        const local = find(n.text);
        if (local) { typesOf(local).forEach(add); origin = `the ${local.kind} ${local.name}`; break; }
        if (constants.has(n.text.toLowerCase())) { const c = constantInfo(n.text); typesOf(c).forEach(add); origin = `the constant ${c.name}`; break; }
        if (n.text === 'true' || n.text === 'false') { add('Bool'); origin = 'true or false'; break; }
        if (/^[A-G][#b]?-?[0-9]$/.test(n.text)) { add('Pitch'); origin = 'a note name'; break; }
        if (/^[A-G][#b]?$/.test(n.text)) { add('PitchClass'); origin = 'a pitch class'; break; }
        for (const [name, t] of Object.entries(NOTE_TYPES)) if ((t.values ?? []).some((v) => v.toLowerCase() === n.text.toLowerCase())) add(name);
        for (const alias of typeAliases) if (alias.members.some((m) => m.toLowerCase() === n.text.toLowerCase())) add(alias.name);
        origin = 'a word of the language';
        break;
      }
      case 'method': { const m = NOTE_BUILTINS[n.name]; if (m?.kind === 'method') { add(m.returns); origin = `the method ${n.name}()`; } break; }
      case 'field': if (infer(n.target, scopes).types.includes('Now')) { add('Int'); origin = `the field ${n.name} of the time`; } break;
      case 'pipeline': { const r = infer(n.steps.length ? n.steps[n.steps.length - 1] : n.source, scopes); r.types.forEach(add); origin = r.origin; break; }
      case 'call': {
        const helper = helpers.get(n.name.toLowerCase());
        if (helper) { if (helper.node.returns) add(helper.node.returns); else (inferred.get(helper.node.name.toLowerCase()) ?? []).forEach(add); origin = `the call of ${helper.node.name}()` + (helper.node.returns ? '' : ' (its type is worked out from its body)'); break; }
        const sig = NOTE_SIGNATURES[n.name];
        if (sig && !['random', 'choose', 'chance'].includes(n.name)) { add(sig.returns); origin = `the call of ${n.name}()`; break; }
        if (['min', 'max', 'abs'].includes(n.name)) { for (const a of n.args.slice(0, n.name === 'abs' ? 1 : 2)) infer(a.kind === 'named' ? a.value : a, scopes).types.forEach(add); origin = `the call of ${n.name}() on the types of its arguments`; break; }
        if (n.name === 'at' && n.args.length) { infer(n.args[0], scopes).types.forEach((t) => add(elementOf(t))); origin = 'an item of a list'; break; }
        if (['random', 'choose', 'chance'].includes(n.name)) {
          if (n.name === 'chance') add('Int');
          else if (n.name === 'choose' && n.args.length && infer(n.args[0], scopes).types.map(elementOf).find(Boolean)) add(infer(n.args[0], scopes).types.map(elementOf).find(Boolean));
          else add(NOTE_SIGNATURES[n.name]?.returns ?? 'Number');
          origin = `the call of ${n.name}()`;
          break;
        }
        const builtin = NOTE_BUILTINS[n.name];
        if (builtin?.kind === 'function') { add(builtin.returns); origin = `the call of ${builtin.id}()`; }
        break;
      }
      default: break;
    }
    return { types: found, origin };
  }

  const mark = (d, start, end, info, keyword = false) => pending.push({ line: d.line, start, end, keyword, info });

  // three rounds: what a helper gives back (worked out from its body) is known to the helpers that call it in the next round
  for (let round = 0; round < 3; round++) {
  constantInfos.clear();
  pending.length = 0;
  for (const d of decls) {
    if (d.node.kind === 'constdecl') {
      const info = constantInfo(d.node.name);
      mark(d, d.node.nameStart, d.node.nameEnd, info);
      mark(d, d.node.start, Math.min(d.node.nameStart, d.node.start + 3), info, true);
      for (const w of descendants(d.node.value)) if (w.kind === 'word' && constants.has(w.text.toLowerCase()) && w.text.toLowerCase() !== d.node.name.toLowerCase()) mark(d, w.start, w.end, constantInfo(w.text));
      continue;
    }
    if (d.node.kind !== 'helper') continue;
    const h = d.node, scopes = [new Map()], returnTypes = [];
    for (const p of h.params) {
      const info = { name: p.name, kind: 'parameter', types: [], declared: p.type, origin: '', valueText: '' };
      scopes[0].set(p.name, info);
      const at = d.text.indexOf(p.name, Math.max(0, p.start ?? 0));
      if (at >= 0) mark(d, at, at + p.name.length, info);
    }
    const references = (n) => {
      if (!n) return;
      for (const w of [n, ...descendants(n)]) {
        if (w.kind !== 'word') continue;
        let hit = null;
        for (let i = scopes.length - 1; i >= 0 && !hit; i--) hit = scopes[i].get(w.text) ?? null;
        if (hit) mark(d, w.start, w.end, hit);
        else if (constants.has(w.text.toLowerCase())) mark(d, w.start, w.end, constantInfo(w.text));
      }
    };
    const block = (b, loop = null) => {
      scopes.push(new Map());
      if (loop) {
        const info = { name: loop.var, kind: 'loop variable', types: [], declared: null, origin: '', valueText: '' };
        const from = infer(loop.from, scopes.slice(0, -1));
        if (loop.to) { info.types.push(...from.types); info.origin = 'the start of the range'; }
        else { for (const t of from.types.map(elementOf)) if (t && !info.types.includes(t)) info.types.push(t); info.origin = 'the items of ' + from.origin; }
        scopes[scopes.length - 1].set(loop.var, info);
        const at = d.text.indexOf(loop.var, loop.start + 3);
        if (at >= 0) mark(d, at, at + loop.var.length, info);
      }
      for (const s of b.statements) {
        switch (s.kind) {
          case 'let': {
            references(s.value);
            const info = { name: s.name, kind: s.mutable ? 'var' : 'let', types: [], declared: s.type ?? null, origin: '', valueText: shorten(d.text.slice(s.value.start, s.value.end)) };
            if (!s.type) { const r = infer(s.value, scopes); info.types.push(...r.types); info.origin = r.origin; }
            scopes[scopes.length - 1].set(s.name, info);
            const at = d.text.indexOf(s.name, s.start + 3);
            if (at >= 0) mark(d, at, at + s.name.length, info);
            mark(d, s.start, s.start + 3, info, true);
            break;
          }
          case 'assign': {
            references(s.value);
            let target = null;
            for (let i = scopes.length - 1; i >= 0 && !target; i--) target = scopes[i].get(s.name) ?? null;
            if (target) {
              if (!target.declared) for (const t of infer(s.value, scopes).types) if (!target.types.includes(t)) target.types.push(t);
              mark(d, s.start, s.start + s.name.length, target);
            }
            break;
          }
          case 'if': for (const br of s.branches) { references(br.cond ?? br[0]); block(br.body ?? br[1]); } if (s.otherwise) block(s.otherwise); break;
          case 'for': references(s.from); references(s.to); block(s.body, s); break;
          case 'repeat': references(s.count); block(s.body); break;
          case 'while': references(s.cond); references(s.max); block(s.body); break;
          case 'return': references(s.value); if (s.value) for (const t of infer(s.value, scopes).types) if (!returnTypes.includes(t)) returnTypes.push(t); break;
          case 'emit': references(s.value); if (!returnTypes.includes('Notes')) returnTypes.push('Notes'); break;
          case 'exprstmt': references(s.value); if (b === h.body && s === b.statements[b.statements.length - 1]) for (const t of infer(s.value, scopes).types) if (!returnTypes.includes(t)) returnTypes.push(t); break;
          default: break;
        }
      }
      scopes.pop();
    };
    block(h.body);
    if (!h.returns) inferred.set(h.name.toLowerCase(), returnTypes);
  }
  }
  for (const p of pending) spots.push({ line: p.line, start: p.start, end: p.end, keyword: p.keyword, detail: detailOf(p.info) });
  spots.returns = new Map([...inferred].filter(([, v]) => v.length).map(([k, v]) => [k, v.join(' | ')]));   // what the helpers without a written return type give back
  return spots;
}

/** The hover text of a variable at a position (the twin of VariableHover): `lines` are the lines of the song, `line` and `col` 0-based; `docs` the doc comments (docsOf). Null when there is no variable there. */
export function variableHover(spots, lines, line, col, word, docs = new Map()) {
  for (const declLine of [...new Set(spots.map((s) => s.line))].filter((l) => l <= line).sort((a, b) => b - a)) {
    let offset = col;
    for (let k = declLine; k < line && k < lines.length; k++) offset += lines[k].length + 1;
    const spot = spots.find((s) => s.line === declLine && offset >= s.start && offset < s.end);
    if (!spot) continue;
    const v = spot.detail;
    if (spot.keyword && NOTE_BUILTINS[word]) return `\`${NOTE_BUILTINS[word].id}\` — keyword\n\n${NOTE_BUILTINS[word].doc}\n\nDeclares \`${v.name}: ${v.type}\`.\n\nBuilt in.`;
    const parts = [`\`${v.kind === 'const' ? 'let' : v.kind} ${v.name}: ${v.type}\``];
    const note = v.kind === 'parameter' ? docs.get(spot.line)?.params.find((p) => p.name.toLowerCase() === v.name.toLowerCase())?.text : '';
    if (note) parts.push(note);
    if (v.valueText) parts.push(`= \`${v.valueText}\``);
    parts.push(v.origin ? `**Type** \`${v.type}\` — ${v.origin === 'the declared type' ? v.origin : 'from ' + v.origin}` : `**Type** \`${v.type}\``);
    if (v.potential.length > 1) parts.push('**Potential types** ' + v.potential.map((t) => `\`${t}\``).join(', ') + (v.kind === 'var' ? ' — it is assigned values of these types.' : '.'));
    parts.push(v.kind === 'const' ? 'constant' : v.kind);
    return parts.join('\n\n');
  }
  return null;
}
