// The JavaScript evaluator of the note pipelines (docs/language/12-note-pipelines.md): `melody.notes |> bars(2..3) |> transpose(+12)` as a pure function from sources and a tempo to compact notes text.
// It must agree byte for byte with the C# evaluator (src/Ample.Core/Notes/NotePipeline.cs); both run spec-tests/note-pipelines/*.json. Exact maths only: decimals are BigInt rationals, time is an integer
// number of micro-beats (1 beat = 1_000_000, 1 bar = 4 beats), seconds convert with the song tempo and round half to even. @ample:note-pipeline

import { typed } from './note-typed.mjs';
import { buildHelperTable, limitsFrom, needsLang, runLang } from './note-lang.mjs';
import { SCALES, UNIT_DENOMINATOR, below, drawBits, explicitKey, forkKey, hex, stableKey, tryChord, tryRoot, unitNumerator } from './note-random.mjs';

export const BEAT = 1_000_000, MAX_EVENTS = 1_000_000;

// ---------- exact numbers

const gcd = (a, b) => { a = a < 0n ? -a : a; b = b < 0n ? -b : b; while (b) [a, b] = [b, a % b]; return a || 1n; };
const rat = (n, d = 1n) => { if (d < 0n) { n = -n; d = -d; } const g = gcd(n, d); return { n: n / g, d: d / g }; };
const mul = (a, b) => rat(a.n * b.n, a.d * b.d);
const add = (a, b) => rat(a.n * b.d + b.n * a.d, a.d * b.d);
const div = (a, b) => rat(a.n * b.d, a.d * b.n);
const sub = (a, b) => rat(a.n * b.d - b.n * a.d, a.d * b.d);
const isInt = (a) => a.d === 1n;
const cmp = (a, b) => { const x = a.n * b.d, y = b.n * a.d; return x < y ? -1 : x > y ? 1 : 0; };

/** Rounds a rational to the nearest integer, ties to the even one. */
function roundHalfEven(q) {
  let whole = q.n / q.d, rest = q.n % q.d;
  if (rest < 0n) { whole -= 1n; rest += q.d; }
  const twice = rest * 2n;
  if (twice > q.d || (twice === q.d && whole % 2n !== 0n)) whole += 1n;
  return whole;
}

function parseDecimal(text) {
  const m = /^([+-]?)(\d+)(?:\.(\d+))?$/.exec(text);
  if (!m) return null;
  const frac = m[3] || '';
  const n = BigInt(m[2] + frac) * (m[1] === '-' ? -1n : 1n);
  return rat(n, 10n ** BigInt(frac.length));
}

/** Micro-beats in one bar: 4 beats, or the beats of the song's meter during an evaluate() call with beatsPerBar (the twin of NotePipeline.UseMeter). */
export let BAR = 4 * BEAT;

/** A quantity in micro-beats (a whole number, rounded half to even). */
function toMicro(q, unit, tempo) {
  const per = { bar: rat(BigInt(BAR)), bars: rat(BigInt(BAR)), beat: rat(BigInt(BEAT)), beats: rat(BigInt(BEAT)), ticks: rat(1n), tick: rat(1n) };
  let micro;
  if (per[unit]) micro = mul(q, per[unit]);
  else if (unit === 's') micro = mul(mul(q, tempo), rat(BigInt(BEAT), 60n));
  else if (unit === 'ms') micro = mul(mul(q, tempo), rat(BigInt(BEAT), 60000n));
  else return null;
  return Number(roundHalfEven(micro));
}

// ---------- the expression parser

/** Replaces a comment (a # up to the end of the line, outside a string) with spaces, so that offsets stay the same. */
export function stripComments(text) {
  let out = '', inString = false, inComment = false;
  for (const c of String(text)) {
    if (inComment) { if (c === '\n') { inComment = false; out += c; } else out += ' '; continue; }
    if (c === '"') inString = !inString;
    if (c === '#' && !inString && !(out.length && /[A-Za-z0-9]/.test(out[out.length - 1]))) { inComment = true; out += ' '; continue; }
    out += c;
  }
  return out;
}

/** Whether the value of a notes parameter is an expression (it starts with a word or a parenthesis) and not a plain note literal. */
export const isExpression = (value) => { const v = stripComments(value).trim().replace(/^"|"$/g, '').trim(); return /^([A-Za-z(]|__c_)/.test(v); };

const ENTRY = /\d+:\d+(?:\.\d+)?:\d+(?:\.\d+)?:\d+:\d+(?::\d+){0,4}(?:;[a-z]+=-?\d+)*/y;
const NUMBER = /[+-]?\d+(?:\.\d+)?/y;
const UNIT = /[A-Za-z]+/y;
const IDENT = /[A-Za-z_][A-Za-z0-9_-]*(?:#[A-Za-z0-9_-]*)?/y;

function tokenize(text, errors) {
  const tokens = [];
  let i = 0;
  const at = (re) => { re.lastIndex = i; const m = re.exec(text); return m && m.index === i ? m[0] : null; };
  while (i < text.length) {
    const c = text[i];
    if (/\s/.test(c)) { i++; continue; }
    if (text.startsWith('|>', i)) { tokens.push({ t: '|>', start: i, end: i + 2 }); i += 2; continue; }
    if (text.startsWith('..=', i)) { tokens.push({ t: '..=', start: i, end: i + 3 }); i += 3; continue; }
    if (text.startsWith('..', i)) { tokens.push({ t: '..', start: i, end: i + 2 }); i += 2; continue; }
    if (c === '"') { const close = text.indexOf('"', i + 1); if (close < 0) { errors.push({ id: 'syntax', message: 'A string is not closed', start: i, end: text.length }); return null; } tokens.push({ t: 'string', v: text.slice(i + 1, close), start: i, end: close + 1 }); i = close + 1; continue; }
    const two = text.slice(i, i + 2);
    if (['->', '==', '!=', '<=', '>=', '&&', '||'].includes(two)) { tokens.push({ t: two, start: i, end: i + 2 }); i += 2; continue; }
    if ('(),.{}='.includes(c)) { tokens.push({ t: c, start: i, end: i + 1 }); i++; continue; }
    const signed = (c === '+' || c === '-') && /\d/.test(text[i + 1] || '');
    if ('<>!*/%[];|?:'.includes(c) || ((c === '+' || c === '-') && !signed)) { tokens.push({ t: c, start: i, end: i + 1 }); i++; continue; }
    if (c === '@') { let h = i + 1; while (h < text.length && /[0-9a-fA-F]/.test(text[h])) h++; if (h > i + 1) { tokens.push({ t: 'hash', v: text.slice(i + 1, h), start: i, end: h }); i = h; continue; } }
    const entry = /\d/.test(c) ? at(ENTRY) : null;
    if (entry) { tokens.push({ t: 'entry', v: entry, start: i, end: i + entry.length }); i += entry.length; continue; }
    const number = at(NUMBER);
    if (number) {
      let end = i + number.length, unit = null;
      const u = (UNIT.lastIndex = end, UNIT.exec(text));
      if (u && u.index === end) { unit = u[0]; end += unit.length; }
      else if (text[end] === '%' && !/[A-Za-z0-9(]/.test(text[end + 1] || '')) { unit = '%'; end++; }
      tokens.push({ t: 'number', v: number, unit, start: i, end });
      i = end; continue;
    }
    const ident = at(IDENT);
    if (ident) { tokens.push({ t: 'ident', v: ident, start: i, end: i + ident.length }); i += ident.length; continue; }
    errors.push({ id: 'syntax', message: `Unexpected "${c}"`, start: i, end: i + 1 });
    return null;
  }
  return tokens;
}

/** Parses the text of an expression into { ast, errors }. */
export function parseExpression(source, options = {}) {
  const errors = [];
  const text = stripComments(source);
  const tokens = tokenize(text, errors);
  if (!tokens) return { ast: null, errors };
  let p = 0;
  const peek = (k = 0) => tokens[p + k];
  const fail = (message, token = peek() || tokens[tokens.length - 1]) => { errors.push({ id: 'syntax', message, start: token?.start ?? 0, end: token?.end ?? text.length }); throw errors; };
  const expect = (t) => { const k = peek(); if (!k || k.t !== t) fail(`Expected "${t}"`, k); p++; return k; };

  const quantity = () => {
    const k = expect('number');
    return { kind: 'quantity', value: parseDecimal(k.v), unit: k.unit, start: k.start, end: k.end, text: k.v + (k.unit || '') };
  };
  const range = () => {
    const from = quantity(), op = peek();
    if (!op || (op.t !== '..' && op.t !== '..=')) fail('Expected ".." in a range', op);
    p++;
    if (!peek()) expect('number');
    const to = binary(0);
    if (to.kind === 'quantity') return { kind: 'range', from, to, inclusive: op.t === '..=', start: from.start, end: to.end };
    return { kind: 'rangeexpr', from, to, inclusive: op.t === '..=', start: from.start, end: to.end };
  };
  const call = () => {
    const name = expect('ident');
    expect('(');
    const args = [];
    if (peek()?.t !== ')') {
      for (;;) {
        args.push(arg());
        if (peek()?.t === ',') { p++; continue; }
        break;
      }
    }
    const close = expect(')');
    return { kind: 'call', name: name.v, args, start: name.start, end: close.end, nameEnd: name.end };
  };
  const arg = () => {
    const k = peek();
    if (k?.t === 'ident' && peek(1)?.t === '=') { p += 2; const value = arg(); return { kind: 'named', name: k.v, value, start: k.start, end: value.end }; }
    if (k?.t === 'string') { p++; return argTail({ kind: 'string', value: k.v, start: k.start, end: k.end }); }
    if (k?.t === 'ident' && !['true', 'false', 'bake'].includes(k.v) && peek(1)?.t !== '.' && peek(1)?.t !== '(' && peek(1)?.t !== '{') { p++; return argTail({ kind: 'word', text: k.v, start: k.start, end: k.end }); }
    if (k?.t === 'number') {
      const after = peek(1);
      if (after && (after.t === '..' || after.t === '..=')) return range();
      return argTail(quantity());
    }
    return pipeline();
  };
  const bake = () => {
    const word = expect('ident'), name = expect('ident');
    const stage = { name: name.v, hasBody: false, strict: false, notes: null, file: null, span: null };
    let end = name.end;
    if (peek()?.t === '{') {
      const open = peek();
      let depth = 1, q = p + 1;
      while (q < tokens.length && depth > 0) { if (tokens[q].t === '{') depth++; else if (tokens[q].t === '}') depth--; q++; }
      if (depth !== 0) fail('The bake body is not closed', open);
      const close = tokens[q - 1];
      p = q; end = close.end; stage.hasBody = true;
      let inNotes = false;
      const notes = [];
      for (const raw of text.slice(open.end, close.start).split('\n')) {
        const line = raw.trim();
        if (!line) continue;
        const m = /^([A-Za-z][A-Za-z-]*)\s*(.*)$/.exec(line), key = m ? m[1] : '', rest = m ? m[2].trim() : '';
        if (key === 'strict') { stage.strict = true; inNotes = false; }
        else if (key === 'input-hash' || key === 'notes-hash') inNotes = false;
        else if (key === 'span') { stage.span = rest; inNotes = false; }
        else if (key === 'file') { stage.file = rest.replace(/^"|"$/g, ''); inNotes = false; }
        else if (key === 'notes') { notes.push(rest); inNotes = true; }
        else if (inNotes) notes.push(line);
      }
      if (notes.length) stage.notes = notes.join(' ').trim();
    }
    return { kind: 'bake', stage, start: word.start, end };
  };
  const parseArgs = () => {
    expect('(');
    const args = [];
    if (peek()?.t !== ')') { for (;;) { args.push(arg()); if (peek()?.t === ',') { p++; continue; } break; } }
    return { args, close: expect(')') };
  };
  const postfix = (node) => {
    while (peek()?.t === '.' && peek(1)?.t === 'ident') {
      p++;
      const name = expect('ident');
      if (peek()?.t === '(') { const { args, close } = parseArgs(); node = { kind: 'method', target: node, name: name.v, args, start: node.start, end: close.end }; }
      else node = { kind: 'field', target: node, name: name.v, start: node.start, end: name.end };
    }
    return node;
  };
  const atom = () => {
    const k = peek();
    if (!k) fail('An expression is empty');
    if (k.t === 'entry') {
      const first = k;
      let last = k;
      const entries = [];
      while (peek()?.t === 'entry') { last = peek(); entries.push(last.v); p++; }
      return { kind: 'literal', entries, start: first.start, end: last.end };
    }
    if (k.t === 'number') return quantity();
    if (k.t === 'string') { p++; return { kind: 'string', value: k.v, start: k.start, end: k.end }; }
    if (k.t === '[') return listLiteral();
    if (k.t === 'ident' && (k.v === 'true' || k.v === 'false')) { p++; return postfix({ kind: 'bool', value: k.v === 'true', start: k.start, end: k.end }); }
    if (k.t === 'ident' && k.v === 'bake') return bakeValue();
    if (k.t === '(' && peek(1)?.t === 'ident' && peek(1).v.includes('-') && peek(2)?.t === ')') {
      const parts = peek(1).v.split('-');
      if (parts.length >= 2 && parts.every((x) => x.length)) {
        const token = peek(1);
        p += 3;
        let at = token.start, result = null;
        for (const part of parts) {
          const operand = /^\d+$/.test(part) ? { kind: 'quantity', value: parseDecimal(part), unit: null, start: at, end: at + part.length, text: part } : { kind: 'word', text: part, start: at, end: at + part.length };
          result = result ? { kind: 'binary', op: '-', left: result, right: operand, start: result.start, end: operand.end } : operand;
          at += part.length + 1;
        }
        return result;
      }
    }
    if (k.t === '(') { p++; const inner = pipeline(); expect(')'); return postfix(inner); }
    if (k.t === 'ident' && k.v === 'editable' && peek(1)?.t === '{') {
      const open = peek(1);
      let depth = 1, q = p + 2;
      while (q < tokens.length && depth > 0) { if (tokens[q].t === '{') depth++; else if (tokens[q].t === '}') depth--; q++; }
      if (depth !== 0) fail('The editable body is not closed', k);
      const close = tokens[q - 1];
      p = q;
      const body = text.slice(open.end, close.start).replace(/^\s*notes\b/, '');
      return { kind: 'literal', entries: body.split(/\s+/).filter(Boolean), editable: true, start: k.start, end: close.end };
    }
    if (k.t === 'ident') {
      if (peek(1)?.t === '.') {
        const block = k; p += 2;
        const port = expect('ident');
        if (peek()?.t === '(') { const { args, close } = parseArgs(); return postfix({ kind: 'method', target: { kind: 'word', text: block.v, start: block.start, end: block.end }, name: port.v, args, start: block.start, end: close.end }); }
        return { kind: 'reference', block: block.v, port: port.v, start: block.start, end: port.end };
      }
      if (peek(1)?.t === '(') return postfix(call());
      p++;
      return postfix({ kind: 'word', text: k.v, start: k.start, end: k.end });
    }
    fail('Expected notes (block.port, a literal or a call)', k);
  };
  const PREC = { '||': 1, '&&': 2, '==': 3, '!=': 3, '<': 3, '<=': 3, '>': 3, '>=': 3, '+': 4, '-': 4, '*': 5, '/': 5, '%': 5 };
  const unary = () => {
    const k = peek();
    if (k && (k.t === '!' || k.t === '-')) { p++; const operand = unary(); return { kind: 'unary', op: k.t, operand, start: k.start, end: operand.end }; }
    return atom();
  };
  const climb = (left, min) => {
    for (;;) {
      const t = peek();
      if (!t) break;
      const signedNumber = t.t === 'number' && t.v.length > 1 && (t.v[0] === '+' || t.v[0] === '-');
      const isWord = t.t === 'ident' && t.v === 'is';
      const prec = signedNumber ? 4 : isWord ? 3 : (PREC[t.t] || 0);
      if (!prec || prec < min) break;
      if (signedNumber) {
        p++;
        const quant = { kind: 'quantity', value: parseDecimal(t.v.slice(1)), unit: t.unit, start: t.start + 1, end: t.end, text: t.v.slice(1) + (t.unit || '') };
        const right = climb(quant, prec + 1);
        left = { kind: 'binary', op: t.v[0], left, right, start: left.start, end: right.end };
        continue;
      }
      p++;
      const right = binary(prec + 1);
      left = { kind: 'binary', op: isWord ? 'is' : t.t, left, right, start: left.start, end: right.end };
    }
    return left;
  };
  const binary = (min) => climb(unary(), min);
  const argTail = (first) => {
    const e = ternaryTail(climb(first, 0));
    if ((peek()?.t === '..' || peek()?.t === '..=') && e.kind !== 'quantity') { const inclusive = peek().t === '..='; p++; const to = binary(0); return { kind: 'rangeexpr', from: e, to, inclusive, start: e.start, end: to.end }; }
    return pipeTail(e);
  };
  const listLiteral = () => {
    if (peek(1)?.t === 'ident' && peek(2)?.t === ']') {
      // [name] is the symbol itself; a one-element list of a name is written [name,]
      const open1 = peek(), sym = peek(1), close1 = peek(2);
      p += 3;
      if (peek()?.t === '.' && peek(1)?.t === 'ident' && peek(2)?.t !== '(') { const port = peek(1); p += 2; return { kind: 'reference', block: sym.v, port: port.v, start: open1.start, end: port.end, escaped: true }; }
      return { kind: 'word', text: sym.v, start: open1.start, end: close1.end, escaped: true };
    }
    const open = expect('[');
    const items = [];
    if (peek()?.t !== ']') { for (;;) { items.push(arg()); if (peek()?.t === ',') { p++; if (peek()?.t === ']') break; continue; } break; } }
    const close = expect(']');
    return { kind: 'list', items, start: open.start, end: close.end };
  };
  const bakeValue = () => {
    const word = expect('ident');
    const expr = binary(0);
    const as = expect('ident');
    if (as.v !== 'as') fail('Expected "as" and the value: bake <expression> as <value>', as);
    const value = binary(0);
    let hash = null, end = value.end, over = false;
    if (peek()?.t === 'hash') { hash = peek().v; end = peek().end; p++; }
    if (peek()?.t === 'ident' && peek().v === 'override') { over = true; end = peek().end; p++; }
    return { kind: 'bakevalue', expr, value, hash, override: over, asStart: as.start, start: word.start, end };
  };

  const KEYWORDS = new Set(['let', 'var', 'if', 'else', 'for', 'in', 'repeat', 'while', 'max', 'stop', 'return', 'emit', 'true', 'false', 'helper', 'dynamic', 'bake', 'as', 'override', 'type', 'is']);
  const typeText = () => {
    const name = expect('ident');
    if (peek()?.t !== '<') return name.v;
    p++;
    const inner = typeText();
    expect('>');
    return name.v + '<' + inner + '>';
  };
  const blockOf = () => {
    const open = expect('{');
    const statements = [];
    let previous = null;
    for (;;) {
      let separated = false;
      while (peek()?.t === ';') { p++; separated = true; }
      const k = peek();
      if (!k) fail('The block is not closed', open);
      if (k.t === '}') { p++; return { kind: 'block', statements, start: open.start, end: k.end }; }
      // two statements on one line need a ";" between them (a statement that ends with a block, like if or for, ends there by itself): "re turn 3" is a typo, not three statements
      if (previous && !['if', 'for', 'repeat', 'while'].includes(previous.kind) && !separated && previous.end <= k.start && !text.slice(previous.end, k.start).includes('\n')) fail('Statements on one line are separated by ";": write ";" before "' + (k.v ?? k.text ?? '') + '", or put it on a new line', k);
      previous = statement();
      statements.push(previous);
    }
  };
  const ifStatement = () => {
    const start = peek();
    const branches = [];
    let otherwise = null, end = start.end;
    for (;;) {
      p++;
      const cond = binary(0), body = blockOf();
      branches.push({ cond, body });
      end = body.end;
      if (!(peek()?.t === 'ident' && peek().v === 'else')) break;
      p++;
      if (peek()?.t === 'ident' && peek().v === 'if') continue;
      otherwise = blockOf();
      end = otherwise.end;
      break;
    }
    return { kind: 'if', branches, otherwise, start: start.start, end };
  };
  const statement = () => {
    const k = peek();
    if (k.t === 'ident') {
      switch (k.v) {
        case 'let': case 'var': {
          p++;
          let type = null;
          if (peek()?.t === 'ident' && peek(1)?.t === 'ident' && peek(2)?.t === '=') type = typeText();
          const name = expect('ident');
          expect('=');
          const value = pipeline();
          return { kind: 'let', name: name.v, type, value, mutable: k.v === 'var', start: k.start, end: value.end };
        }
        case 'if': return ifStatement();
        case 'for': {
          p++;
          const name = expect('ident'), inWord = expect('ident');
          if (inWord.v !== 'in') fail('Expected "in"', inWord);
          const from = binary(0);
          let to = null, inclusive = false;
          if (peek()?.t === '..' || peek()?.t === '..=') { inclusive = peek().t === '..='; p++; to = binary(0); }
          const body = blockOf();
          return { kind: 'for', var: name.v, from, to, inclusive, body, start: k.start, end: body.end };
        }
        case 'repeat': { p++; const count = binary(0), body = blockOf(); return { kind: 'repeat', count, body, start: k.start, end: body.end }; }
        case 'while': {
          p++;
          const cond = binary(0);
          let max = null, stop = false;
          if (peek()?.t === 'ident' && peek().v === 'max') { p++; max = binary(0); if (peek()?.t === 'ident' && peek().v === 'stop') { p++; stop = true; } }
          const body = blockOf();
          return { kind: 'while', cond, max, stop, body, start: k.start, end: body.end };
        }
        case 'return': {
          p++;
          const after = peek();
          if (!after || after.t === '}' || after.t === ';') return { kind: 'return', value: null, start: k.start, end: k.end };
          const value = pipeline();
          return { kind: 'return', value, start: k.start, end: value.end };
        }
        case 'emit': { p++; const value = pipeline(); return { kind: 'emit', value, start: k.start, end: value.end }; }
        default: break;
      }
      if (peek(1)?.t === '=' && !KEYWORDS.has(k.v)) { p += 2; const value = pipeline(); return { kind: 'assign', name: k.v, value, start: k.start, end: value.end }; }
      if (peek(1)?.t === 'ident' && peek(2)?.t === '=' && !KEYWORDS.has(k.v) && !KEYWORDS.has(peek(1).v)) {
        const type = typeText(), name = expect('ident');
        expect('=');
        const value = pipeline();
        return { kind: 'let', name: name.v, type, value, mutable: false, start: k.start, end: value.end };
      }
    }
    const expression = pipeline();
    return { kind: 'exprstmt', value: expression, start: expression.start, end: expression.end };
  };
  const declaration = () => {
    const first = peek();
    if (!first) fail('The declaration is empty');
    let dynamic = false;
    if (first.t === 'ident' && first.v === 'dynamic') { p++; dynamic = true; }
    const kw = expect('ident');
    let result;
    if (kw.v === 'helper') {
      const name = expect('ident');
      expect('(');
      const params = [];
      if (peek()?.t !== ')') {
        for (;;) {
          const type = typeText(), pn = expect('ident');
          let def = null;
          if (peek()?.t === '=') { p++; def = arg(); }
          params.push({ name: pn.v, type, default: def, start: pn.start, end: pn.end });
          if (peek()?.t === ',') { p++; continue; }
          break;
        }
      }
      expect(')');
      let returns = null, baked = null;
      if (peek()?.t === '->') { p++; returns = typeText(); }
      if (peek()?.t === 'ident' && peek().v === 'bake') { p++; const as = expect('ident'); if (as.v !== 'as') fail('Expected: bake as <value>', as); baked = arg(); }
      const body = blockOf();
      result = { kind: 'helper', name: name.v, dynamic, params, returns, baked, body, start: first.start, end: body.end, nameStart: name.start, nameEnd: name.end };
    } else if (kw.v === 'type' && !dynamic) {
      const name = expect('ident');
      expect('=');
      const members = [typeText()];
      while (peek()?.t === '|' || (peek()?.t === 'ident' && peek().v === 'or')) { p++; members.push(typeText()); }
      result = { kind: 'typedecl', name: name.v, members, start: first.start, end: tokens[p - 1].end };
    } else if (kw.v === 'let' && !dynamic) {
      let type = null;
      if (peek()?.t === 'ident' && peek(1)?.t === 'ident' && peek(2)?.t === '=') type = typeText();
      const name = expect('ident');
      expect('=');
      const value = pipeline();
      result = { kind: 'constdecl', name: name.v, type, value, start: first.start, end: value.end, nameStart: name.start, nameEnd: name.end };
    } else fail('Expected helper, dynamic helper, type or let', kw);
    if (p < tokens.length) fail('Unexpected input after the declaration', peek());
    return result;
  };
  const pipeline = () => pipeTail(ternaryTail(binary(0)));
  /** The rest of a conditional expression after its condition: ? then : otherwise (right associative; both branches are whole pipelines). The twin of TernaryTail in NotePipeline.cs. */
  const ternaryTail = (cond) => {
    if (peek()?.t !== '?') return cond;
    p++;
    const then = pipeline();
    expect(':');
    const otherwise = pipeline();
    return { kind: 'ternary', cond, then, otherwise, start: cond.start, end: otherwise.end };
  };
  const pipeTail = (node) => {
    const steps = [];
    while (peek()?.t === '|>') { p++; steps.push(peek()?.t === 'ident' && peek().v === 'bake' ? bake() : call()); }
    return steps.length ? { kind: 'pipeline', source: node, steps, start: node.start, end: steps[steps.length - 1].end } : node;
  };
  try {
    if (options.declaration) return { ast: declaration(), errors };
    const ast = pipeline();
    if (p < tokens.length) fail('Unexpected input after the expression', peek());
    return { ast: typed(ast), errors };
  } catch (e) {
    if (e === errors) return { ast: null, errors };
    throw e;
  }
}

/** Parses one declaration of a file (a helper, a type alias or a constant): { ast, errors }. */
export const parseDeclaration = (text) => parseExpression(text, { declaration: true });

/** The helpers, type aliases and constants of a file as a table for evaluate(): sources is [{ text, line }]. */
export const helperTableFrom = (sources) => buildHelperTable(sources, parseDeclaration);

// ---------- the values

const SELECT = new Set(['bars', 'beats', 'time']);
const names = ['bars', 'beats', 'time', 'mask', 'remove', 'cut', 'replace', 'transpose', 'shift', 'concat', 'merge', 'reverse', 'stretch', 'legato', 'quantize', 'humanize', 'repeat', 'arp', 'where', 'euclid', 'randomize', 'thin', 'scramble', 'clamp', 'fold', 'snap', 'keep', 'avoid', 'walk', 'chords', 'scale', 'set'];

function nearest(word, list) {
  const dist = (a, b) => { const row = Array.from({ length: b.length + 1 }, (_, i) => i); for (let i = 1; i <= a.length; i++) { let prev = row[0]; row[0] = i; for (let j = 1; j <= b.length; j++) { const keep = row[j]; row[j] = Math.min(row[j] + 1, row[j - 1] + 1, prev + (a[i - 1] === b[j - 1] ? 0 : 1)); prev = keep; } } return row[b.length]; };
  let best = null, bestD = 3;
  for (const w of list) { const d = dist(word, w); if (d < bestD) { best = w; bestD = d; } }
  return best;
}

const entryOf = /^(\d+):(\d+(?:\.\d+)?):(\d+(?:\.\d+)?):(\d+):(\d+)((?::\d+){0,4})((?:;[a-z]+=-?\d+)*)$/;

/** Compact notes text -> events (exact micro-beats); the routing fields and the `;key=value` suffixes ride along untouched. Entries it cannot read are errors. */
export function readNotes(text, errors, where = null) {
  const events = [];
  for (const word of String(text || '').trim().split(/\s+/).filter(Boolean)) {
    const m = entryOf.exec(word);
    if (!m) { errors.push({ id: 'bad-notes', message: `Not a note: ${word}`, ...(where || {}) }); continue; }
    const bar = Number(m[1]), beat = parseDecimal(m[2]), len = parseDecimal(m[3]);
    if (bar < 1) { errors.push({ id: 'bad-notes', message: `Bar must be 1 or more: ${word}`, ...(where || {}) }); continue; }
    events.push({ at: (bar - 1) * BAR + Number(roundHalfEven(mul(beat, rat(BigInt(BEAT))))), len: Math.max(1, Number(roundHalfEven(mul(len, rat(BigInt(BEAT)))))), pitch: Number(m[4]), vel: Number(m[5]), tail: m[6], ext: m[7] });
  }
  return events;
}

/** A value is never shorter than its notes: the span grows (in whole bars) to cover the furthest note end. */
const grown = (value) => { const end = value.events.reduce((m, e) => Math.max(m, e.at + e.len), 0); value.span = Math.max(value.span, Math.ceil(end / BAR) * BAR); return value; };
const spanOfEvents = (events) => Math.max(1, Math.ceil(events.reduce((end, e) => Math.max(end, e.at + e.len), 0) / BAR)) * BAR;

function formatMicro(micro) {
  const whole = Math.floor(micro / BEAT), frac = micro % BEAT;
  return frac ? `${whole}.${String(frac).padStart(6, '0').replace(/0+$/, '')}` : String(whole);
}

const TIE_BASE = 1000000, MAX_TIE = 2000000;
const hasTie = (e) => e.ext.includes(';tie=') || (e.tail.match(/:/g) || []).length >= 4;
const EXT_ORDER = ['pan', 'fine', 'rel', 'cut', 'slide', 'port', 'bend', 'tie'];
const EXT_DEFAULT = { pan: 0, fine: 0, rel: -1, cut: 100, slide: 0, port: 0, bend: 0 };
const SET_RANGE = { pitch: [0, 127], velocity: [1, 127], pan: [-100, 100], fine: [-100, 100], rel: [-1, 5000], cut: [0, 100], slide: [0, 1], port: [0, 2000], bend: [-2400, 2400], channel: [-1, 15], bank: [0, 16383], program: [0, 127] };
const setTail = (tail, key, value) => {
  // channel -1 takes the routing away (the note plays with the channel, bank and program of the block)
  if (key === 'channel' && value < 0) return '';
  const parts = tail.split(':').filter(Boolean);
  while (parts.length < 3) parts.push('0');
  parts[key === 'channel' ? 0 : key === 'bank' ? 1 : 2] = String(value);
  return ':' + parts.join(':');
};
const setExt = (ext, key, value) => {
  let pairs = ext.split(';').filter(Boolean).map((x) => { const eq = x.indexOf('='); return [x.slice(0, eq), x.slice(eq + 1)]; }).filter((x) => x[0] !== key);
  if (!(key in EXT_DEFAULT) || EXT_DEFAULT[key] !== value) pairs.push([key, String(value)]);
  // known keys in a fixed order, so that the same settings always give the same text
  pairs = pairs.map((x, i) => ({ x, i, at: EXT_ORDER.indexOf(x[0]) < 0 ? Infinity : EXT_ORDER.indexOf(x[0]) })).sort((a, b) => (a.at === b.at ? a.i - b.i : a.at - b.at)).map((y) => y.x);
  return pairs.map((x) => `;${x[0]}=${x[1]}`).join('');
};

/** Events -> compact text: canonical order, a note that crosses a bar line is written as consecutive pieces that share a tie id. */
export function writeNotes(events) {
  const ordered = events.map((e, i) => ({ e, i })).sort((a, b) => a.e.at - b.e.at || a.e.pitch - b.e.pitch || a.i - b.i).map((x) => x.e);
  const out = [];
  let tie = 0;
  for (const e of ordered) {
    let start = e.at, left = e.len;
    // a note over a bar line gets a tie id of its own (unless it has one), so that its pieces play as one note
    const tied = left > BAR - (start % BAR) && !hasTie(e) && TIE_BASE + tie <= MAX_TIE ? `;tie=${TIE_BASE + tie++}` : '';
    while (left > 0) {
      const take = Math.min(left, BAR - (start % BAR));
      out.push(`${Math.floor(start / BAR) + 1}:${formatMicro(start % BAR)}:${formatMicro(take)}:${e.pitch}:${e.vel}${e.tail}${e.ext}${tied}`);
      start += take; left -= take;
    }
  }
  return out.join(' ');
}

/**
 * Evaluates an expression. `sources` maps a block name to its notes (compact text, or { events, span }); `tempo` is the song tempo (a number or a decimal string).
 * Returns { notes, span, warnings, errors, value }; on errors `notes` is null. Warnings and errors are { id, message, start, end, suggestion? }.
 */
export function evaluate(text, options = {}) {
  const previous = BAR;
  const beats = Number(options.beatsPerBar);
  BAR = Number.isFinite(beats) && beats > 0 && beats !== 4 ? Math.round(beats * BEAT) : 4 * BEAT;
  try { return evaluateCore(text, options); } finally { BAR = previous; }
}

function evaluateCore(text, { sources = {}, tempo = 120, limit = MAX_EVENTS, guard = new Set(), master = 0, streams: streamDefinitions = null, helpers = null, limits = null, clock = null, osRandom = null, capabilities = null, environment = null } = {}) {
  const parsed = parseExpression(text);
  const errors = [...parsed.errors], warnings = [];
  if (!parsed.ast) return { notes: null, span: null, warnings, errors, value: null };
  const bpm = parseDecimal(String(tempo)) || rat(120n);
  const problem = (list, id, message, node, extra = {}) => list.push({ id, message, start: node?.start ?? 0, end: node?.end ?? text.length, ...extra });
  const cache = new Map();
  const sourceValue = (node) => {
    const key = node.block;
    if (Object.prototype.hasOwnProperty.call(sources, key) && sources[key]?.needsServer) { problem(errors, 'needs-server', `"${key}" is read by the server`, node); return null; }
    if (!Object.prototype.hasOwnProperty.call(sources, key)) { problem(errors, 'unknown-reference', `There is no block "${key}"`, node); return null; }
    if (!cache.has(key)) {
      const src = sources[key];
      if (src && typeof src === 'object' && typeof src.notes === 'string' && isExpression(src.notes)) {
        if (guard.has(key)) { problem(errors, 'reference-cycle', `"${key}" reads itself through its notes`, node); return null; }
        guard.add(key);
        const inner = evaluate(src.notes, { sources, tempo, limit, guard, master, streams: src.streams || null, helpers, limits, clock, osRandom, capabilities, environment });
        guard.delete(key);
        if (inner.errors.length) { problem(errors, 'unknown-reference', `The notes of "${key}" have problems`, node); return null; }
        cache.set(key, { events: inner.value.events, span: inner.span });
      } else if (typeof src === 'string' && isExpression(src)) {
        if (guard.has(key)) { problem(errors, 'reference-cycle', `"${key}" reads itself through its notes`, node); return null; }
        guard.add(key);
        const inner = evaluate(src, { sources, tempo, limit, guard, master, helpers, limits, clock, osRandom, capabilities, environment });
        guard.delete(key);
        if (inner.errors.length) { problem(errors, 'unknown-reference', `The notes of "${key}" have problems`, node); return null; }
        cache.set(key, { events: inner.value.events, span: inner.span });
      } else {
        const events = typeof src === 'string' ? readNotes(src.trim().replace(/^"|"$/g, ''), errors, { start: node.start, end: node.end }) : src.events.map((e) => ({ ...e }));
        cache.set(key, { events, span: typeof src === 'string' || src.span == null ? spanOfEvents(events) : src.span });
      }
    }
    const v = cache.get(key);
    return { events: v.events.map((e) => ({ ...e })), span: v.span };
  };

  const micro = (q, unit, node) => {
    const value = toMicro(q, unit, bpm);
    if (value === null) problem(errors, 'wrong-unit', `Unknown unit "${unit}"`, node);
    return value;
  };
  /** A range value -> [from, to) in micro-beats, or null. `how` is the coordinate system: bars, beats or time. */
  const window = (node, how) => {
    if (node.kind !== 'range') { problem(errors, 'bad-argument', 'A range like 1..5 is expected', node); return null; }
    const coord = (q, end) => {
      let value = q.value;
      if (how === 'bars') {
        if (q.unit && q.unit !== 'bars' && q.unit !== 'bar') { problem(errors, 'wrong-unit', 'Bar positions have no unit', q); return null; }
        if (!isInt(value)) problem(warnings, 'fractional-bar', 'A fractional bar position', q);
        if (end && node.inclusive && isInt(value)) value = rat(value.n + 1n);
        return micro(sub(value, rat(1n)), 'bars', q);
      }
      if (how === 'beats') {
        if (q.unit && q.unit !== 'beats' && q.unit !== 'beat') { problem(errors, 'wrong-unit', 'Beat positions have no unit', q); return null; }
        if (end && node.inclusive && isInt(value)) value = rat(value.n + 1n);
        return micro(sub(value, rat(1n)), 'beats', q);
      }
      if (!q.unit) { problem(errors, 'missing-unit', 'Seconds or milliseconds need a unit (s or ms)', q); return null; }
      return micro(value, q.unit, q);
    };
    const from = coord(node.from, false), to = coord(node.to, true);
    if (from === null || to === null) return null;
    if (to < from) { problem(errors, 'range-reversed', 'The end of the range is before its start', node); return null; }
    return [from, to];
  };
  const rangeArg = (arg) => {
    if (arg?.kind === 'range') return window(arg, 'bars');
    if (arg?.kind === 'call' && SELECT.has(arg.name) && arg.args.length === 1) return window(arg.args[0], arg.name);
    problem(errors, 'bad-argument', 'A range like bars(1..5) is expected', arg);
    return null;
  };
  const notesArg = (arg) => { const v = evalNode(arg); return v; };
  const arity = (node, count) => { if (node.args.length !== count) { problem(errors, 'bad-argument', `${node.name} takes ${count} argument${count === 1 ? '' : 's'}`, node); return false; } return true; };

  /** Positional and named arguments of an operator, by name; null (with a problem) when something is wrong. */
  const argMap = (node, list, required) => {
    const map = {};
    let position = 0;
    for (const a of node.args) {
      let key, value;
      if (a.kind === 'named') {
        if (!list.includes(a.name)) { problem(errors, 'bad-argument', `${node.name} has no argument "${a.name}" (it has: ${list.join(', ')})`, a); return null; }
        key = a.name; value = a.value;
      } else {
        if (position >= list.length) { problem(errors, 'bad-argument', `${node.name} takes at most ${list.length} arguments`, a); return null; }
        key = list[position++]; value = a;
      }
      if (key in map) { problem(errors, 'bad-argument', `"${key}" is given twice`, a); return null; }
      map[key] = value;
    }
    for (let i = 0; i < required; i++) if (!(list[i] in map)) { problem(errors, 'bad-argument', `${node.name} needs "${list[i]}"`, node); return null; }
    return map;
  };
  const whole = (n, what, min, max) => {
    if (n?.kind !== 'quantity' || n.unit || !isInt(n.value) || n.value.n < BigInt(min) || n.value.n > BigInt(max)) { problem(errors, 'bad-argument', `${what} must be a whole number from ${min} to ${max}`, n); return null; }
    return Number(n.value.n);
  };
  const lengthOf = (n) => {
    if (n?.kind !== 'quantity') { problem(errors, 'bad-argument', 'A length like 0.5beat or 20ms is expected', n); return null; }
    if (!n.unit) { problem(errors, 'missing-unit', 'A length needs a unit (bars, beats, s, ms or ticks)', n); return null; }
    return micro(n.value, n.unit, n);
  };
  const euclid = (node) => {
    const m = argMap(node, ['hits', 'steps', 'pitch', 'step', 'velocity', 'rotate'], 2);
    if (!m) return null;
    const hits = whole(m.hits, 'hits', 0, 256), steps = whole(m.steps, 'steps', 1, 256);
    if (hits === null || steps === null) return null;
    if (hits > steps) { problem(errors, 'bad-argument', 'hits cannot be more than steps', node); return null; }
    let pitch = 36, velocity = 100, rotate = 0, stepLen = BEAT;
    if (m.pitch && (pitch = whole(m.pitch, 'pitch', 0, 127)) === null) return null;
    if (m.velocity && (velocity = whole(m.velocity, 'velocity', 1, 127)) === null) return null;
    if (m.rotate && (rotate = whole(m.rotate, 'rotate', -256, 256)) === null) return null;
    if (m.step) { stepLen = lengthOf(m.step); if (stepLen === null) return null; if (stepLen <= 0) { problem(errors, 'bad-argument', 'step must be longer than zero', m.step); return null; } }
    const events = [];
    for (let i = 0; i < steps; i++) {
      const j = (((i + rotate) % steps) + steps) % steps;
      if ((j * hits) % steps < hits) events.push({ at: i * stepLen, len: stepLen, pitch, vel: velocity, tail: '', ext: '' });
    }
    return { events, span: Math.max(1, Math.ceil((steps * stepLen) / BAR)) * BAR };
  };
  // ---- seeds and random values (docs/language/13-randomness.md)
  const masterSeed = BigInt(master);
  const uses = [];
  let streamStates = null;
  const quant = (value, unit, at) => ({ kind: 'quantity', value, unit, start: at.start, end: at.end, text: '' });
  const ratText = (v) => (v.d === 1n ? String(v.n) : `${v.n}/${v.d}`);
  const seedText = (n) => {
    if (n.kind === 'quantity') return ratText(n.value);
    if (n.kind === 'call') return n.name === 'stable' && n.args.length === 1 && n.args[0].kind === 'string' ? `stable("${n.args[0].value}")` : `${n.name}()`;
    if (n.kind === 'method') return `${seedText(n.target)}.${n.name}(${n.args.map((a) => (a.kind === 'string' ? `"${a.value}"` : a.kind === 'quantity' ? ratText(a.value) : '?')).join(', ')})`;
    if (n.kind === 'word') return n.text;
    return '?';
  };
  const streams = () => {
    if (streamStates) return streamStates;
    streamStates = new Map();
    for (const [name, definition] of Object.entries(streamDefinitions || {})) {
      const parsedSeed = parseExpression(definition);
      if (!parsedSeed.ast) errors.push({ id: 'stream-error', message: `random-stream ${name}: ${parsedSeed.errors[0]?.message || 'not a seed'}`, start: 0, end: 1 });
      else streamStates.set(name, { name, seedText: definition.trim(), seedNode: parsedSeed.ast, use: null, cursor: 0 });
    }
    return streamStates;
  };
  const tryKey = (node) => {
    switch (node.kind) {
      case 'quantity':
        if (!node.unit && isInt(node.value) && node.value.n >= 0n && node.value.n <= (1n << 64n) - 1n) return { key: explicitKey(node.value.n), text: seedText(node) };
        break;
      case 'call':
        if (node.name === 'stable') {
          if (node.args.length === 1 && node.args[0].kind === 'string' && node.args[0].value.length > 0) return { key: stableKey(masterSeed, node.args[0].value), text: seedText(node) };
          problem(errors, 'seed-error', 'stable(...) takes one name in quotes, like stable("verse")', node);
          return null;
        }
        if (node.name === 'random-seed') { problem(errors, 'unresolved-random-seed', 'randomSeed() is not a number yet: pick one (the fix writes it into the song) so that the result can be reproduced', node); return null; }
        break;
      case 'method':
        if (node.name === 'fork') {
          if (node.args.length !== 1) { problem(errors, 'seed-error', 'fork(...) takes one label, like .fork("chords") or .fork(3)', node); return null; }
          const a = node.args[0];
          const label = a.kind === 'string' && a.value.length > 0 ? a.value : a.kind === 'quantity' && !a.unit && isInt(a.value) && a.value.n >= 0n ? `#${a.value.n}` : null;
          if (label === null) { problem(errors, 'seed-error', 'A fork label is a name in quotes or a whole number', a); return null; }
          const parent = tryKey(node.target);
          return parent ? { key: forkKey(parent.key, label), text: seedText(node) } : null;
        }
        break;
      case 'word':
        if (streams().has(node.text)) { problem(errors, 'stream-error', `"${node.text}" is a random-stream, not a seed: read it with ${node.text}.next()`, node); return null; }
        break;
      default:
    }
    problem(errors, 'seed-error', 'A seed is a whole number, stable("name"), or one of those followed by .fork("label")', node);
    return null;
  };
  const seedArg = (m, node, op) => {
    if (!m.seed) { problem(errors, 'missing-seed', `${op} needs a seed, for example seed=stable("verse") or seed=7: the result must be the same every time`, node); return null; }
    const k = tryKey(m.seed);
    if (!k) return null;
    const use = { operator: op, seed: k.text, key: k.key, draws: 0, start: m.seed.start, end: m.seed.end };
    uses.push(use);
    return use;
  };
  const draw = (use, purpose, address) => { use.draws++; return drawBits(use.key, purpose, address); };
  const addresses = (events) => {
    const result = new Array(events.length), seen = new Map();
    events.map((e, i) => i).sort((a, b) => events[a].at - events[b].at || events[a].pitch - events[b].pitch || a - b).forEach((i) => {
      const k = `${events[i].at}:${events[i].pitch}`, dup = seen.get(k) || 0;
      seen.set(k, dup + 1);
      result[i] = `n:${events[i].at}:${events[i].pitch}:${dup}`;
    });
    return result;
  };
  const inRange = (range, bits, at) => {
    if (!range) return quant(rat(unitNumerator(bits), UNIT_DENOMINATOR), null, at);
    if (range.kind !== 'range') { problem(errors, 'bad-argument', 'A range like 0..100 is expected', range); return null; }
    if (!range.from.unit !== !range.to.unit) { problem(errors, 'missing-unit', 'Both ends of the range need the same kind of unit', range); return null; }
    if (range.from.unit) {
      const lo = micro(range.from.value, range.from.unit, range.from), hi = micro(range.to.value, range.to.unit, range.to);
      if (lo === null || hi === null) return null;
      const top = hi + (range.inclusive ? 1 : 0);
      if (top <= lo) { problem(errors, 'range-reversed', 'The end of the range is not after its start', range); return null; }
      return quant(rat(BigInt(lo + Number(below(bits, top - lo)))), 'ticks', at);
    }
    if (isInt(range.from.value) && isInt(range.to.value)) {
      const lo = range.from.value.n, top = range.to.value.n + (range.inclusive ? 1n : 0n);
      if (top <= lo) { problem(errors, 'range-reversed', 'The end of the range is not after its start', range); return null; }
      return quant(rat(lo + below(bits, top - lo)), null, at);
    }
    const low = range.from.value, high = range.to.value;
    if (high.n * low.d <= low.n * high.d) { problem(errors, 'range-reversed', 'The end of the range is not after its start', range); return null; }
    return quant(add(low, mul(sub(high, low), rat(unitNumerator(bits), UNIT_DENOMINATOR))), null, at);
  };
  const valueCall = (c) => {
    const positional = [], named = {};
    for (const a of c.args) {
      if (a.kind === 'named') {
        if (!['seed', 'round'].includes(a.name) || a.name in named) { problem(errors, 'bad-argument', `${c.name} takes seed= and round= as named arguments, each once`, a); return null; }
        named[a.name] = a.value;
      } else positional.push(a);
    }
    const use = seedArg(named, c, c.name);
    if (!use) return null;
    let round = 0;
    if (named.round && (round = whole(named.round, 'round', 0, 9007199254740991)) === null) return null;
    const address = String(round);
    if (c.name === 'random') {
      if (positional.length !== 1) { problem(errors, 'bad-argument', 'random takes one range, like random(0..100, seed=...)', c); return null; }
      return inRange(positional[0], draw(use, 'random', address), c);
    }
    if (c.name === 'choose') {
      if (positional.length < 1) { problem(errors, 'bad-argument', 'choose needs the values to choose from', c); return null; }
      return positional[Number(below(draw(use, 'choose', address), positional.length))];
    }
    const p = positional[0];
    if (positional.length !== 1 || p.kind !== 'quantity' || p.unit || p.value.n < 0n || p.value.n > p.value.d) { problem(errors, 'bad-argument', 'chance takes a probability from 0 to 1, like chance(0.25, seed=...)', c); return null; }
    const hit = draw(use, 'chance', address) * p.value.d < p.value.n * (1n << 64n);
    return quant(rat(hit ? 1n : 0n), null, c);
  };
  const streamNext = (w, m) => {
    const state = streams().get(w.text);
    if (!state) { problem(errors, 'stream-error', `There is no random-stream "${w.text}": declare it in the clip with  random-stream ${w.text} stable("name")`, m); return null; }
    if (m.args.length > 1) { problem(errors, 'bad-argument', 'next() takes no argument, or one range', m); return null; }
    if (!state.use) {
      const before = errors.length, k = tryKey(state.seedNode);
      for (let i = before; i < errors.length; i++) errors[i] = { ...errors[i], start: m.start, end: m.end, message: `random-stream ${w.text}: ${errors[i].message}` };
      if (!k) return null;
      state.use = { operator: `stream:${w.text}`, seed: k.text, key: k.key, draws: 0, start: m.start, end: m.end };
      uses.push(state.use);
    }
    const bits = draw(state.use, 'random', String(state.cursor));
    state.cursor++;
    return inRange(m.args.length === 1 ? m.args[0] : null, bits, m);
  };
  const scalar = (a) => {
    if (a.kind === 'named') { if (a.name === 'seed') return a; const v = scalar(a.value); return v ? { ...a, value: v } : null; }
    if (a.kind === 'call' && ['random', 'choose', 'chance'].includes(a.name)) return valueCall(a);
    if (a.kind === 'method' && a.name === 'next') {
      if (a.target.kind === 'word') return streamNext(a.target, a);
      problem(errors, 'stream-error', 'next() belongs to a random-stream: declare one in the clip (random-stream name <seed>) and call name.next(); a plain seed has no cursor', a);
      return null;
    }
    return a;
  };
  const resolveArgs = (node) => {
    const args = [];
    for (const a of node.args) { const r = scalar(a); if (!r) return null; args.push(r); }
    return { ...node, args };
  };
  const intRange = (n, what, min, max) => {
    if (n?.kind !== 'range' || n.from.unit || n.to.unit || !isInt(n.from.value) || !isInt(n.to.value)) { problem(errors, 'bad-argument', `${what} takes a range of whole numbers, like ${what}=48..72 (or 48..=72 to include 72)`, n); return null; }
    const lo = Number(n.from.value.n), top = Number(n.to.value.n) + (n.inclusive ? 1 : 0);
    if (top <= lo) { problem(errors, 'range-reversed', 'The end of the range is not after its start', n); return null; }
    if (lo < min || top - 1 > max) { problem(errors, 'bad-argument', `${what} must stay between ${min} and ${max}`, n); return null; }
    return { lo, top };
  };
  const wordOf = (n) => (n?.kind === 'word' ? n.text : n?.kind === 'string' ? n.value : null);
  const scaleArgs = (m) => {
    const rootText = wordOf(m.root), scaleText = wordOf(m.scale);
    const root = rootText === null ? null : tryRoot(rootText);
    if (root === null) { problem(errors, 'bad-argument', 'A root is a note name like C, Eb or "F#"', m.root); return null; }
    const degrees = scaleText === null ? undefined : SCALES[scaleText.toLowerCase()];
    if (!degrees) { problem(errors, 'bad-argument', `Unknown scale. Scales: ${Object.keys(SCALES).join(', ')}`, m.scale); return null; }
    return { root, degrees };
  };
  const degreePitch = (root, degrees, octave, index) => {
    const len = degrees.length, shift = Math.floor(index / len), within = ((index % len) + len) % len;
    return 12 * (octave + 1) + root + degrees[within] + 12 * shift;
  };
  const spanFor = (count, stepLen) => Math.max(1, Math.ceil((count * stepLen) / BAR)) * BAR;
  const randomize = (value, node) => {
    const m = argMap(node, ['velocity', 'pitch', 'seed'], 0);
    if (!m) return null;
    if (!m.velocity && !m.pitch) { problem(errors, 'bad-argument', 'randomize needs velocity=lo..hi and/or pitch=lo..hi (pitch in semitones around the note)', node); return null; }
    let v = null, p = null;
    if (m.velocity && !(v = intRange(m.velocity, 'velocity', 1, 127))) return null;
    if (m.pitch && !(p = intRange(m.pitch, 'pitch', -48, 48))) return null;
    const use = seedArg(m, node, 'randomize');
    if (!use) return null;
    const address = addresses(value.events);
    return { events: value.events.map((e, i) => {
      let r = e;
      if (v) r = { ...r, vel: v.lo + Number(below(draw(use, 'randomize.velocity', address[i]), v.top - v.lo)) };
      if (p) r = { ...r, pitch: Math.max(0, Math.min(127, r.pitch + p.lo + Number(below(draw(use, 'randomize.pitch', address[i]), p.top - p.lo)))) };
      return r;
    }), span: value.span };
  };
  const thin = (value, node) => {
    const m = argMap(node, ['p', 'seed', 'per'], 1);
    if (!m) return null;
    const p = m.p;
    if (p.kind !== 'quantity' || p.unit || p.value.n < 0n || p.value.n > p.value.d) { problem(errors, 'bad-argument', 'thin takes the chance to drop a note, from 0 to 1, like thin(0.25, seed=...)', p); return null; }
    let per = 'note';
    if (m.per) { if (m.per.kind === 'word' && ['note', 'bar'].includes(m.per.text)) per = m.per.text; else { problem(errors, 'bad-argument', 'per is note or bar', m.per); return null; } }
    const use = seedArg(m, node, 'thin');
    if (!use) return null;
    const address = addresses(value.events);
    return { events: value.events.filter((e, i) => !(draw(use, 'thin', per === 'bar' ? `bar:${Math.floor(e.at / BAR)}` : address[i]) * p.value.d < p.value.n * (1n << 64n))), span: value.span };
  };
  /** set(pitch="38 50 45", velocity=100, pan=...): puts a value, or a cycle of values, on every note in the order of the notes (start, then pitch). */
  const setValues = (key, n) => {
    const [min, max] = SET_RANGE[key];
    let tokens;
    if (n?.kind === 'quantity' && !n.unit && isInt(n.value)) tokens = [String(n.value.n)];
    else if (wordOf(n) !== null) tokens = wordOf(n).split(/[\s,]+/).filter(Boolean);
    else { problem(errors, 'bad-argument', `${key} takes a whole number or a quoted list of them, like ${key}="1 2 3"`, n); return null; }
    if (tokens.length === 0 || tokens.length > 4096) { problem(errors, 'bad-argument', `${key} needs between 1 and 4096 values`, n); return null; }
    const found = [];
    for (const token of tokens) {
      let v = null;
      const name = key === 'pitch' ? /^([A-Ga-g][#b]?)(-?\d)$/.exec(token) : null;
      if (name) {
        const root = tryRoot(name[1][0].toUpperCase() + name[1].slice(1));
        if (root !== null) v = 12 * (Number(name[2]) + 1) + root;
      }
      if (v === null) {
        if (!/^[+-]?\d+$/.test(token)) { problem(errors, 'bad-argument', `"${token}" is not a value of ${key}${key === 'pitch' ? ' (a number or a note name like C4 or F#3)' : ''}`, n); return null; }
        v = Number(token);
      }
      if (v < min || v > max) { problem(errors, 'bad-argument', `${key} must be from ${min} to ${max} (found ${v})`, n); return null; }
      found.push(v);
    }
    return found;
  };
  const setFields = (value, node) => {
    const names = ['pitch', 'velocity', 'pan', 'fine', 'rel', 'cut', 'slide', 'port', 'bend', 'channel', 'bank', 'program'];
    const m = argMap(node, names, 0);
    if (!m) return null;
    if (Object.keys(m).length === 0) { problem(errors, 'bad-argument', `set needs at least one of: ${names.join(', ')}`, node); return null; }
    const lists = [];
    for (const key of names.filter((k) => k in m)) {
      const values = setValues(key, m[key]);
      if (!values) return null;
      lists.push([key, values]);
    }
    const order = value.events.map((e, i) => i).sort((a, b) => value.events[a].at - value.events[b].at || value.events[a].pitch - value.events[b].pitch || a - b);
    const result = value.events.map((e) => ({ ...e }));
    order.forEach((index, rank) => {
      let e = value.events[index];
      for (const [key, values] of lists) {
        const v = values[rank % values.length];
        e = key === 'pitch' ? { ...e, pitch: v } : key === 'velocity' ? { ...e, vel: v } : key === 'channel' || key === 'bank' || key === 'program' ? { ...e, tail: setTail(e.tail, key, v) } : { ...e, ext: setExt(e.ext, key, v) };
      }
      result[index] = e;
    });
    return { events: result, span: value.span };
  };
  const scramble = (value, node) => {
    const m = argMap(node, ['seed'], 0);
    const use = m && seedArg(m, node, 'scramble');
    if (!use) return null;
    const address = addresses(value.events);
    const canonical = value.events.map((e, i) => i).sort((a, b) => value.events[a].at - value.events[b].at || value.events[a].pitch - value.events[b].pitch || a - b);
    const keys = canonical.map((i) => draw(use, 'scramble', address[i]));
    const order = canonical.map((x, j) => j).sort((a, b) => (keys[a] < keys[b] ? -1 : keys[a] > keys[b] ? 1 : a - b));
    const result = value.events.map((e) => ({ ...e }));
    canonical.forEach((target, j) => { result[target] = { ...value.events[target], pitch: value.events[canonical[order[j]]].pitch }; });
    return { events: result, span: value.span };
  };
  const snapKeepAvoid = (value, node) => {
    const m = argMap(node, ['root', 'scale'], 2);
    const found = m && scaleArgs(m);
    if (!found) return null;
    const { root, degrees } = found, inScale = (pitch) => degrees.includes((((pitch - root) % 12) + 12) % 12);
    if (node.name === 'keep') return { events: value.events.filter((e) => inScale(e.pitch)), span: value.span };
    if (node.name === 'avoid') return { events: value.events.filter((e) => !inScale(e.pitch)), span: value.span };
    return { events: value.events.map((e) => {
      const rel = (((e.pitch - root) % 12) + 12) % 12;
      let best = 0, bestDistance = Infinity;
      for (const d of degrees) {
        let delta = d - rel;
        if (delta > 6) delta -= 12;
        if (delta <= -6) delta += 12;
        if (Math.abs(delta) < bestDistance || (Math.abs(delta) === bestDistance && delta < best)) { best = delta; bestDistance = Math.abs(delta); }
      }
      return { ...e, pitch: Math.max(0, Math.min(127, e.pitch + best)) };
    }), span: value.span };
  };
  const clampFold = (value, node) => {
    if (!arity(node, 1)) return null;
    const r = intRange(node.args[0], 'range', 0, 127);
    if (!r) return null;
    const lo = r.lo, hi = r.top - 1;
    if (node.name === 'clamp') return { events: value.events.map((e) => ({ ...e, pitch: Math.max(lo, Math.min(hi, e.pitch)) })), span: value.span };
    if (hi - lo < 11) { problem(errors, 'bad-argument', 'fold needs a range of at least one octave (12 semitones), like fold(48..=71)', node.args[0]); return null; }
    return { events: value.events.map((e) => { let p = e.pitch; while (p > hi) p -= 12; while (p < lo) p += 12; return { ...e, pitch: p }; }), span: value.span };
  };
  const chords = (g) => {
    const symbols = [], named = {};
    for (const a of g.args) {
      if (a.kind === 'named') {
        if (!['length', 'octave', 'velocity'].includes(a.name) || a.name in named) { problem(errors, 'bad-argument', 'chords takes length=, octave= and velocity=, each once', a); return null; }
        named[a.name] = a.value;
      } else {
        const w = wordOf(a);
        if (w === null) {
          // after the chord symbols, the next values are length, octave and velocity in that order (the ones not given by name)
          const slot = ['length', 'octave', 'velocity'].find((n) => !(n in named));
          if (symbols.length === 0 || slot === undefined) { problem(errors, 'bad-argument', 'chords takes the chord symbols first, like chords("Am F C G"), then length, octave and velocity (by name or in that order)', a); return null; }
          named[slot] = a;
          continue;
        }
        symbols.push(...w.split(/\s+/).filter(Boolean));
      }
    }
    if (symbols.length < 1 || symbols.length > 256) { problem(errors, 'bad-argument', 'chords needs between 1 and 256 chord symbols', g); return null; }
    let length = BAR, octave = 4, velocity = 90;
    if (named.length) { length = lengthOf(named.length); if (length === null) return null; if (length <= 0) { problem(errors, 'bad-argument', 'length must be longer than zero', named.length); return null; } }
    if (named.octave && (octave = whole(named.octave, 'octave', 0, 8)) === null) return null;
    if (named.velocity && (velocity = whole(named.velocity, 'velocity', 1, 127)) === null) return null;
    const events = [];
    for (let i = 0; i < symbols.length; i++) {
      const chord = tryChord(symbols[i]);
      if (!chord) { problem(errors, 'bad-argument', `"${symbols[i]}" is not a chord symbol (examples: C, Am, F#m7, Bbmaj7, Gsus4, E7)`, g); return null; }
      for (const interval of chord.intervals) events.push({ at: i * length, len: length, pitch: Math.max(0, Math.min(127, 12 * (octave + 1) + chord.root + interval)), vel: velocity, tail: '', ext: '' });
    }
    return { events, span: spanFor(symbols.length, length) };
  };
  const scaleRun = (g) => {
    const m = argMap(g, ['root', 'scale', 'steps', 'step', 'octave', 'velocity'], 2);
    const found = m && scaleArgs(m);
    if (!found) return null;
    let steps = 8, stepLen = BEAT / 2, octave = 4, velocity = 100;
    if (m.steps && (steps = whole(m.steps, 'steps', 1, 256)) === null) return null;
    if (m.step) { stepLen = lengthOf(m.step); if (stepLen === null) return null; if (stepLen <= 0) { problem(errors, 'bad-argument', 'step must be longer than zero', m.step); return null; } }
    if (m.octave && (octave = whole(m.octave, 'octave', 0, 8)) === null) return null;
    if (m.velocity && (velocity = whole(m.velocity, 'velocity', 1, 127)) === null) return null;
    const events = [];
    for (let i = 0; i < steps; i++) events.push({ at: i * stepLen, len: stepLen, pitch: Math.max(0, Math.min(127, degreePitch(found.root, found.degrees, octave, i))), vel: velocity, tail: '', ext: '' });
    return { events, span: spanFor(steps, stepLen) };
  };
  const walk = (g) => {
    const m = argMap(g, ['root', 'scale', 'steps', 'step', 'octave', 'range', 'velocity', 'seed'], 2);
    const found = m && scaleArgs(m);
    if (!found) return null;
    let steps = 16, stepLen = BEAT / 2, octave = 4, range = 2, velocity = 100;
    if (m.steps && (steps = whole(m.steps, 'steps', 1, 256)) === null) return null;
    if (m.step) { stepLen = lengthOf(m.step); if (stepLen === null) return null; if (stepLen <= 0) { problem(errors, 'bad-argument', 'step must be longer than zero', m.step); return null; } }
    if (m.octave && (octave = whole(m.octave, 'octave', 0, 8)) === null) return null;
    if (m.range && (range = whole(m.range, 'range', 1, 7)) === null) return null;
    if (m.velocity && (velocity = whole(m.velocity, 'velocity', 1, 127)) === null) return null;
    const use = seedArg(m, g, 'walk');
    if (!use) return null;
    const events = [], len = found.degrees.length;
    let index = 0;
    for (let i = 0; i < steps; i++) {
      if (i > 0) index = Math.max(-len, Math.min(2 * len, index + Number(below(draw(use, 'walk', String(i)), 2 * range + 1)) - range));
      events.push({ at: i * stepLen, len: stepLen, pitch: Math.max(0, Math.min(127, degreePitch(found.root, found.degrees, octave, index))), vel: velocity, tail: '', ext: '' });
    }
    return { events, span: spanFor(steps, stepLen) };
  };
  const generate = (node) => {
    const g = resolveArgs(node);
    if (!g) return null;
    if (g.name === 'euclid') return euclid(g);
    if (g.name === 'chords') return chords(g);
    if (g.name === 'scale') return scaleRun(g);
    return walk(g);
  };
  const humanize = (value, node) => {
    const m = argMap(node, ['timing', 'velocity', 'seed'], 0);
    if (!m) return null;
    let timing = 0, velocity = 0;
    if (m.timing) { timing = lengthOf(m.timing); if (timing === null || timing < 0) return null; }
    if (m.velocity && (velocity = whole(m.velocity, 'velocity', 0, 127)) === null) return null;
    const use = seedArg(m, node, 'humanize');
    if (!use) return null;
    const address = addresses(value.events);
    return grown({ events: value.events.map((e, i) => {
      const dt = timing > 0 ? Number(below(draw(use, 'humanize.timing', address[i]), 2 * timing + 1)) - timing : 0;
      const dv = velocity > 0 ? Number(below(draw(use, 'humanize.velocity', address[i]), 2 * velocity + 1)) - velocity : 0;
      return { ...e, at: Math.max(0, e.at + dt), vel: Math.max(1, Math.min(127, e.vel + dv)) };
    }), span: value.span });
  };
  const finish = () => {
    streams();   // streams that were declared but never read are noted too
    const seen = new Set();
    for (const u of uses.filter((x) => ['humanize', 'randomize', 'thin', 'scramble'].includes(x.operator))) {
      const id = `${u.operator}:${u.key}`;
      if (seen.has(id)) warnings.push({ id: 'same-seed', message: `${u.operator} uses the same seed as an earlier ${u.operator} in this clip, so their random results are identical`, start: u.start, end: u.end });
      seen.add(id);
    }
    for (const s of (streamStates ? [...streamStates.values()] : []).filter((x) => errors.length === 0 && x.cursor === 0 && !x.use)) warnings.push({ id: 'unused-stream', message: `The random-stream "${s.name}" is declared but never used`, start: 0, end: 1 });
  };
  const provenance = () => ({
    seeds: uses.map((u) => ({ operator: u.operator, seed: u.seed, key: hex(u.key), draws: u.draws })),
    streams: (streamStates ? [...streamStates.values()] : []).map((s) => ({ name: s.name, seed: s.seedText, key: s.use ? hex(s.use.key) : '', consumed: s.cursor })),
  });
  const step = (value, node) => {
    const name = node.name;
    if (!names.includes(name)) { problem(errors, 'unknown-operator', `Unknown operator "${name}"`, { start: node.start, end: node.nameEnd }, { suggestion: nearest(name, names) }); return null; }
    const resolved = resolveArgs(node);
    if (!resolved) return null;
    node = resolved;
    if (SELECT.has(name)) {
      if (!arity(node, 1)) return null;
      const w = window(node.args[0], name);
      return w && selectWindow(value, w, true);
    }
    if (name === 'mask') { if (!arity(node, 1)) return null; const w = rangeArg(node.args[0]); return w && selectWindow(value, w, false); }
    if (name === 'remove' || name === 'cut') {
      if (!arity(node, 1)) return null;
      const w = rangeArg(node.args[0]);
      if (!w) return null;
      const [w0, w1] = w, gap = w1 - w0;
      const events = value.events.filter((e) => e.at < w0 || e.at >= w1).map((e) => (name === 'cut' && e.at >= w1 ? { ...e, at: e.at - gap } : e));
      return { events, span: name === 'cut' ? Math.max(0, value.span - gap) : value.span };
    }
    if (name === 'replace') {
      if (!arity(node, 2)) return null;
      const w = rangeArg(node.args[0]), other = notesArg(node.args[1]);
      if (!w || !other) return null;
      const kept = value.events.filter((e) => e.at < w[0] || e.at >= w[1]);
      return grown({ events: [...kept, ...other.events.map((e) => ({ ...e, at: e.at + w[0] }))], span: value.span });
    }
    if (name === 'transpose') {
      if (!arity(node, 1)) return null;
      const a = node.args[0];
      if (a.kind !== 'quantity' || !isInt(a.value) || a.unit) { problem(errors, 'bad-argument', 'transpose takes whole semitones, like +12', a); return null; }
      const semitones = Math.max(-48, Math.min(48, Number(a.value.n)));
      return { events: value.events.map((e) => ({ ...e, pitch: Math.max(0, Math.min(127, e.pitch + semitones)) })), span: value.span };
    }
    if (name === 'shift') {
      if (!arity(node, 1)) return null;
      const a = node.args[0];
      if (a.kind !== 'quantity') { problem(errors, 'bad-argument', 'shift takes a length like +250ms', a); return null; }
      if (!a.unit) { problem(errors, 'missing-unit', 'A length needs a unit (bars, beats, s, ms or ticks)', a); return null; }
      const by = micro(a.value, a.unit, a);
      if (by === null) return null;
      const moved = value.events.map((e) => ({ ...e, at: e.at + by })), before = moved.filter((e) => e.at < 0).length;
      if (before) problem(warnings, 'before-origin', `${before} note${before === 1 ? '' : 's'} start before the beginning and were dropped`, node);
      return grown({ events: moved.filter((e) => e.at >= 0), span: value.span });
    }
    if (name === 'reverse') { if (!arity(node, 0)) return null; return { events: value.events.map((e) => ({ ...e, at: Math.max(0, value.span - (e.at + e.len)) })), span: value.span }; }
    if (name === 'stretch') {
      if (!arity(node, 1)) return null;
      const q = node.args[0];
      if (q.kind !== 'quantity' || q.unit || q.value.n <= 0n || q.value.n * 64n < q.value.d || q.value.n > 64n * q.value.d) { problem(errors, 'bad-argument', 'stretch takes a factor between 1/64 and 64, like 2 (twice as slow) or 0.5', q); return null; }
      const scale = (x) => Number(roundHalfEven(mul(rat(BigInt(x)), q.value)));
      return grown({ events: value.events.map((e) => ({ ...e, at: scale(e.at), len: Math.max(1, scale(e.len)) })), span: scale(value.span) });
    }
    if (name === 'legato') {
      if (!arity(node, 0)) return null;
      const ordered = value.events.map((e, i) => ({ e, i })).sort((a, b) => a.e.at - b.e.at || a.e.pitch - b.e.pitch || a.i - b.i).map((x) => x.e);
      const starts = [...new Set(ordered.map((e) => e.at))];
      return { events: ordered.map((e) => { const at = starts.indexOf(e.at); return at + 1 < starts.length ? { ...e, len: starts[at + 1] - e.at } : e; }), span: value.span };
    }
    if (name === 'quantize') {
      if (!arity(node, 1)) return null;
      const g = node.args[0];
      if (g.kind !== 'quantity' || !g.unit) { problem(errors, 'missing-unit', 'quantize takes a grid length with a unit, like 0.25beat', g); return null; }
      const grid = micro(g.value, g.unit, g);
      if (grid === null) return null;
      if (grid <= 0) { problem(errors, 'bad-argument', 'The grid must be longer than zero', g); return null; }
      return grown({ events: value.events.map((e) => ({ ...e, at: Number(roundHalfEven(rat(BigInt(e.at), BigInt(grid)))) * grid })), span: value.span });
    }
    if (name === 'humanize') return humanize(value, node);
    if (name === 'randomize') return randomize(value, node);
    if (name === 'thin') return thin(value, node);
    if (name === 'scramble') return scramble(value, node);
    if (name === 'set') return setFields(value, node);
    if (name === 'clamp' || name === 'fold') return clampFold(value, node);
    if (name === 'snap' || name === 'keep' || name === 'avoid') return snapKeepAvoid(value, node);
    if (name === 'repeat') {
      if (!arity(node, 1)) return null;
      const times = whole(node.args[0], 'times', 1, 1024);
      if (times === null) return null;
      if (value.events.length * times > limit) { problem(errors, 'cap-events', `repeat(${times}) would make ${value.events.length * times} notes; the limit is ${limit}`, node); return null; }
      const copies = [];
      for (let k = 0; k < times; k++) for (const e of value.events) copies.push({ ...e, at: e.at + k * value.span });
      return { events: copies, span: value.span * times };
    }
    if (name === 'arp') {
      const m = argMap(node, ['rate'], 1);
      if (!m) return null;
      const rate = lengthOf(m.rate);
      if (rate === null) return null;
      if (rate <= 0) { problem(errors, 'bad-argument', 'The rate must be longer than zero', node); return null; }
      const groups = new Map();
      for (const e of value.events) { if (!groups.has(e.at)) groups.set(e.at, []); groups.get(e.at).push(e); }
      const events = [];
      for (const at of [...groups.keys()].sort((a, b) => a - b)) {
        const chord = groups.get(at).map((e, i) => ({ e, i })).sort((a, b) => a.e.pitch - b.e.pitch || a.i - b.i).map((x) => x.e);
        if (chord.length < 2) { events.push(...chord); continue; }
        const length = Math.max(...chord.map((e) => e.len)), slots = Math.max(1, Math.floor(length / rate));
        if (events.length + slots > limit) { problem(errors, 'cap-events', `arp would make more than ${limit} notes`, node); return null; }
        for (let k = 0; k < slots; k++) events.push({ ...chord[k % chord.length], at: at + k * rate, len: Math.min(rate, length - k * rate) });
      }
      return grown({ events, span: value.span });
    }
    if (name === 'where') {
      const m = argMap(node, ['pitch', 'velocity'], 0);
      if (!m) return null;
      if (!Object.keys(m).length) { problem(errors, 'bad-argument', 'where needs pitch=lo..hi and/or velocity=lo..hi', node); return null; }
      const bounds = (key) => {
        if (!m[key]) return [0, 127];
        const r = m[key];
        if (r.kind !== 'range' || r.from.unit || r.to.unit || !isInt(r.from.value) || !isInt(r.to.value)) { problem(errors, 'bad-argument', `${key} takes a range of whole numbers, like ${key}=48..72 (or 48..=72 to include 72)`, r); return null; }
        return [Number(r.from.value.n), Number(r.to.value.n) - (r.inclusive ? 0 : 1)];
      };
      const p = bounds('pitch'), v = bounds('velocity');
      if (!p || !v) return null;
      return { events: value.events.filter((e) => e.pitch >= p[0] && e.pitch <= p[1] && e.vel >= v[0] && e.vel <= v[1]), span: value.span };
    }
    if (['euclid', 'walk', 'chords', 'scale'].includes(name)) { problem(errors, 'bad-argument', `${name}(...) makes notes, it does not work on notes: start a pipeline with it`, node); return null; }
    if (name === 'concat' || name === 'merge') {
      if (!arity(node, 1)) return null;
      const other = notesArg(node.args[0]);
      if (!other) return null;
      if (name === 'concat') return { events: [...value.events, ...other.events.map((e) => ({ ...e, at: e.at + value.span }))], span: value.span + other.span };
      return grown({ events: [...value.events, ...other.events], span: Math.max(value.span, other.span) });
    }
    return null;
  };

  const selectWindow = (value, [w0, w1], rebase) => {
    const events = [];
    for (const e of value.events) {
      if (e.at < w0 || e.at >= w1) continue;
      events.push({ ...e, at: rebase ? e.at - w0 : e.at, len: Math.max(1, Math.min(e.len, w1 - e.at)) });
    }
    return { events, span: rebase ? w1 - w0 : value.span };
  };

  const evalNode = (node) => {
    let value = null;
    if (node.kind === 'reference') { value = sourceValue(node); if (value && node.port !== 'notes') { problem(errors, 'bad-argument', `"${node.port}" is not a notes output; use ${node.block}.notes`, node); value = null; } }
    else if (node.kind === 'value') value = { events: node.value.events.map((e) => ({ ...e })), span: node.value.span };
    else if (node.kind === 'literal') { const events = readNotes(node.entries.join(' '), errors, { start: node.start, end: node.end }); value = { events, span: spanOfEvents(events) }; }
    else if (node.kind === 'pipeline') {
      value = evalNode(node.source);
      let from = 0;
      node.steps.forEach((s, i) => { if (s.kind === 'bake' && s.stage.hasBody && (s.stage.notes !== null || s.stage.file)) from = i + 1; });
      if (value && from > 0) {
        const stored = node.steps[from - 1];
        if (stored.stage.notes === null) { problem(errors, 'needs-server', `The bake stage "${stored.stage.name}" keeps its notes in a file`, stored); value = null; }
        else {
          const events = readNotes(stored.stage.notes, errors, { start: stored.start, end: stored.end });
          let span = spanOfEvents(events);
          const sm = stored.stage.span && /^(\d+(?:\.\d+)?)\s*([a-z]*)$/.exec(stored.stage.span);
          if (sm) { const q = parseDecimal(sm[1]), micro = q && toMicro(q, sm[2] || 'bars', bpm); if (micro !== null && micro !== undefined) span = micro; }
          value = { events, span };
        }
      }
      for (let i = from; i < node.steps.length && value; i++) { if (node.steps[i].kind === 'bake') continue; value = step(value, node.steps[i]); }
    } else if (node.kind === 'call' && ['euclid', 'walk', 'chords', 'scale'].includes(node.name)) {
      value = generate(node);
    } else if (node.kind === 'call') {
      problem(errors, 'bad-argument', `${node.name}(...) needs notes to work on: write notes |> ${node.name}(...)`, node);
    } else problem(errors, 'bad-argument', 'Notes are expected here', node);
    if (value && value.events.length > limit) { problem(errors, 'cap-events', `More than ${limit} notes`, node); return null; }
    return value;
  };

  const table = helpers || buildHelperTable([], parseDeclaration);
  let lang = null, value;
  if (needsLang(parsed.ast, table, names)) {
    const kit = {
      errors, warnings, bpm, table, text, limit, BEAT,
      limits: limits || {},
      clock, osRandom, capabilities, environment,
      math: { rat, mul, add, sub, isInt, roundHalfEven, parseDecimal, toMicro },
      operatorNames: names,
      nearest: (word, list) => nearest(word, list),
      distance: (a, b) => { const row = Array.from({ length: b.length + 1 }, (_, i) => i); for (let i = 1; i <= a.length; i++) { let prev = row[0]; row[0] = i; for (let j = 1; j <= b.length; j++) { const keep = row[j]; row[j] = Math.min(row[j] + 1, row[j - 1] + 1, prev + (a[i - 1] === b[j - 1] ? 0 : 1)); prev = keep; } } return row[b.length]; },
      evalNode, step, generate, valueCall, streamNext, streams: streamDefinitions, seedText,
      storedNotes: (stored) => {
        const events = readNotes(stored.stage.notes, errors, { start: stored.start, end: stored.end });
        let span = spanOfEvents(events);
        const sm = stored.stage.span && /^(\d+(?:\.\d+)?)\s*([a-z]*)$/.exec(stored.stage.span);
        if (sm) { const qty = parseDecimal(sm[1]), micro = qty && toMicro(qty, sm[2] || 'bars', bpm); if (micro !== null && micro !== undefined) span = micro; }
        return { events, span };
      },
    };
    lang = runLang(parsed.ast, kit);
    value = lang.value;
  } else value = evalNode(parsed.ast);
  finish();
  const langInfo = lang ? { dynamic: lang.dynamic, bakedDynamic: lang.bakedDynamic, dynamicInputs: lang.dynamicInputs, fuel: lang.fuel } : { dynamic: false, bakedDynamic: false, dynamicInputs: [], fuel: 0 };
  if (errors.length || !value) return { notes: null, span: null, warnings, errors, value: null, ...provenance(), ...langInfo };
  return { notes: writeNotes(value.events), span: value.span, warnings, errors, value, ...provenance(), ...langInfo };
}

/** The hand-written editable { } pieces of an expression in the order they stand: { start, end, entries }. */
export function editables(source) {
  const { ast } = parseExpression(source);
  const found = [];
  const walk = (node) => {
    if (!node) return;
    if (node.kind === 'literal' && node.editable) found.push({ start: node.start, end: node.end, entries: node.entries.join(' ') });
    else if (node.kind === 'pipeline') { walk(node.source); node.steps.forEach(walk); }
    else if (node.kind === 'call') node.args.forEach(walk);
    else if (node.kind === 'named') walk(node.value);
  };
  walk(ast);
  return found.sort((a, b) => a.start - b.start);
}

/** The seeds of an expression in the order they stand: [{ start, end, text, stable }] (every seed=... argument); stable is the name at the root of the seed, or null. */
export function seedSites(source) {
  const { ast } = parseExpression(source), clean = stripComments(source), found = [];
  const root = (n) => (n?.kind === 'call' && n.name === 'stable' && n.args.length === 1 && n.args[0].kind === 'string' ? n.args[0].value : n?.kind === 'method' ? root(n.target) : null);
  const walk = (node) => {
    if (!node) return;
    if (node.kind === 'named' && node.name === 'seed') found.push({ start: node.value.start, end: node.value.end, text: clean.slice(node.value.start, node.value.end), stable: root(node.value) });
    else if (node.kind === 'named') walk(node.value);
    else if (node.kind === 'pipeline') { walk(node.source); node.steps.forEach(walk); }
    else if (node.kind === 'call') node.args.forEach(walk);
    else if (node.kind === 'method') { walk(node.target); node.args.forEach(walk); }
  };
  walk(ast);
  return found.sort((a, b) => a.start - b.start);
}

/** The stable name at the root of a seed written as text (a random-stream line), or null. */
export function stableNameOf(seedText) {
  const { ast } = parseExpression(seedText);
  const root = (n) => (n?.kind === 'call' && n.name === 'stable' && n.args.length === 1 && n.args[0].kind === 'string' ? n.args[0].value : n?.kind === 'method' ? root(n.target) : null);
  return root(ast);
}
