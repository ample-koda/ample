// Block comments (/* ... */) and doc comments (/** ... */ or lines that start with ##) of a song, and the hover text built from them. The twin of Comments.cs and of the hover of Queries.cs
// (docs/language/18-comments-and-doc-comments.md): the editor shows the same without the language server. @ample:doc-comments

import { NOTE_TYPES } from './note-catalog.mjs';

const word = (c) => /[A-Za-z0-9]/.test(c ?? '');
const BUILTIN_TYPES = new Set(['Int', 'Float', 'Bool', 'String', 'Text', 'Number', 'Notes', 'Note', 'Seed', 'List', 'Length', 'Time', 'Duration', 'Interval', 'Gain', 'Percent']);

/** Splits a type text at a separator that is not inside angle brackets (the twin of DocTypes.cs). */
export function splitTop(text, separator) {
  const parts = [];
  let depth = 0, start = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] === '<') depth++;
    else if (text[i] === '>') depth = Math.max(0, depth - 1);
    else if (text[i] === separator && depth === 0) { parts.push(text.slice(start, i).trim()); start = i + 1; }
  }
  parts.push(text.slice(start).trim());
  return parts.filter(Boolean);
}

/** The type as a set of canonical members: unions as members, aliases written out (`alias(name)` gives their members or null), `?` dropped, case ignored, the arguments of a generic canonical too. */
export function canonicalType(type, alias, depth = 0) {
  const set = new Set();
  for (const raw of splitTop(String(type), '|')) {
    const member = raw.replace(/\?$/, '').trim();
    const open = member.indexOf('<');
    if (open > 0 && member.endsWith('>')) {
      const args = splitTop(member.slice(open + 1, -1), ',').map((a) => '{' + [...canonicalType(a, alias, depth)].sort().join('|') + '}');
      set.add(member.slice(0, open).trim().toLowerCase() + '<' + args.join(',') + '>');
    } else {
      const members = depth < 4 ? alias(member) : null;
      if (members) for (const m of members) for (const x of canonicalType(m, alias, depth + 1)) set.add(x);
      else set.add(member.toLowerCase());
    }
  }
  return set;
}

/** Every type name used in a type, the arguments of generics included. */
export function typeNames(type) {
  const names = [];
  for (const raw of splitTop(String(type), '|')) {
    const member = raw.replace(/\?$/, '').trim();
    const open = member.indexOf('<');
    if (open > 0 && member.endsWith('>')) { names.push(member.slice(0, open).trim()); for (const arg of splitTop(member.slice(open + 1, -1), ',')) names.push(...typeNames(arg)); }
    else names.push(member);
  }
  return names;
}

/** The first word of a tag text, which can contain spaces inside angle brackets. */
function takeWord(rest) {
  let depth = 0, i = 0;
  for (; i < rest.length; i++) {
    if (rest[i] === '<') depth++;
    else if (rest[i] === '>') depth = Math.max(0, depth - 1);
    else if (rest[i] === ' ' && depth === 0) break;
  }
  return { first: rest.slice(0, i), rest: rest.slice(i).trimStart() };
}

/** Takes an optional type off the front of the text of a tag: {Interval|Int} in braces, or a bare word that is a type of the language, a union (with |), a generic (List<Int>) or an optional type (Int?). Gives { type, rest }. */
function takeType(rest) {
  if (rest.startsWith('{')) {
    let depth = 0, close = -1;
    for (let i = 0; i < rest.length && close < 0; i++) { if (rest[i] === '{') depth++; else if (rest[i] === '}' && --depth === 0) close = i; }
    if (close > 0) return { type: rest.slice(1, close).replace(/\s+/g, ''), rest: rest.slice(close + 1).trimStart() };
  }
  const { first, rest: remaining } = takeWord(rest);
  if (first && (first.includes('|') || first.includes('<') || first.endsWith('?') || NOTE_TYPES[first] || BUILTIN_TYPES.has(first))) return { type: first.replace(/\s+/g, ''), rest: remaining };
  return { type: null, rest };
}

export function blankBlocks(text) {
  const src = String(text);
  const ranges = [];
  if (!src.includes('/*')) return { text: src, unterminated: -1, ranges };
  let out = '', inString = false, inLine = false, inBlock = false, line = 0, openedAt = -1, lineStart = 0, pieceStart = -1;
  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (c === '\n') {
      if (inBlock && pieceStart >= 0) ranges.push({ line, start: pieceStart, length: i - lineStart - pieceStart });
      line++; lineStart = i + 1; pieceStart = inBlock ? 0 : -1; inString = false; inLine = false; out += c;
      continue;
    }
    if (inBlock) {
      if (c === '*' && src[i + 1] === '/') { inBlock = false; out += '  '; i++; ranges.push({ line, start: pieceStart, length: i + 1 - lineStart - pieceStart }); pieceStart = -1; }
      else out += c === '\r' ? c : ' ';
      continue;
    }
    if (inLine) { out += c; continue; }
    if (c === '"') inString = !inString;
    else if (!inString && c === '#' && !word(out[out.length - 1])) inLine = true;
    else if (!inString && c === '/' && src[i + 1] === '*' && !word(out[out.length - 1])) { inBlock = true; openedAt = line; pieceStart = i - lineStart; out += '  '; i++; continue; }
    out += c;
  }
  let unterminated = -1;
  if (inBlock) { unterminated = openedAt; if (pieceStart >= 0) ranges.push({ line, start: pieceStart, length: src.length - lineStart - pieceStart }); }
  return { text: out, unterminated, ranges };
}

/** Reads the lines of a doc comment (without the comment marks): { summary, params: [{ name, text }], returns, examples, deprecated }. */
export function parseDoc(body) {
  const summary = [], params = [], examples = [];
  let returns = null, deprecated = null, returnType = null, tag = '';
  for (const raw of body) {
    const line = raw.replace(/\s+$/, '');
    const trimmed = line.trimStart();
    if (trimmed.startsWith('@')) {
      const space = trimmed.indexOf(' ');
      tag = (space < 0 ? trimmed : trimmed.slice(0, space)).toLowerCase();
      const rest = space < 0 ? '' : trimmed.slice(space + 1).trim();
      if (tag === '@param') { const typed = takeType(rest); const [name = '', ...more] = typed.rest.split(/\s+/).filter(Boolean); params.push({ name: name.replace(/^\$/, ''), text: more.join(' ').replace(/^[-\s]+/, ''), type: typed.type }); }
      else if (tag === '@return' || tag === '@returns') { const typed = takeType(rest); returns = typed.rest; returnType = typed.type; }
      else if (tag === '@example') examples.push(rest);
      else if (tag === '@deprecated') deprecated = rest;
      else tag = '';
      continue;
    }
    const t = line.trim();
    if (tag === '@param' && params.length && t) params[params.length - 1].text = (params[params.length - 1].text + ' ' + t).trim();
    else if ((tag === '@return' || tag === '@returns') && t) returns = ((returns ?? '') + ' ' + t).trim();
    else if (tag === '@example' && examples.length) examples[examples.length - 1] = examples[examples.length - 1] ? examples[examples.length - 1] + '\n' + line : line;
    else if (tag === '@deprecated' && t) deprecated = ((deprecated ?? '') + ' ' + t).trim();
    else if (tag === '') summary.push(line);
  }
  return { summary: summary.join('\n').trim(), params, returns, returnType, examples: examples.map((e) => e.replace(/^\n+|\n+$/g, '').trim()), deprecated };
}

/** The doc comments of the lines of a text (the original lines, comments included), by the 0-based line of the declaration under them. */
export function docsOf(lines) {
  const found = new Map();
  const clean = (s) => s.trim().replace(/^\*+/, '').replace(/^ /, '');
  for (let i = 0; i < lines.length; i++) {
    const trimmed = lines[i].trim();
    const body = [];
    let last;
    if (trimmed.startsWith('/**') && !trimmed.startsWith('/**/')) {
      last = i;
      let rest = trimmed.slice(3);
      for (;;) {
        const close = rest.indexOf('*/');
        if (close >= 0) { body.push(clean(rest.slice(0, close))); break; }
        body.push(clean(rest));
        if (++last >= lines.length) return found;   // never closed: reported elsewhere
        rest = lines[last].trim();
      }
    } else if (trimmed.startsWith('##')) {
      last = i;
      while (last < lines.length && lines[last].trim().startsWith('##')) { body.push(lines[last].trim().replace(/^#+/, '').replace(/^ /, '')); last++; }
      last--;
    } else continue;
    const next = last + 1;
    if (next < lines.length && lines[next].trim() && !lines[next].trimStart().startsWith('#') && !lines[next].trimStart().startsWith('/*')) found.set(next, parseDoc(body));
    i = last;
  }
  return found;
}

/** The comment as markdown: a deprecation note, the summary, what is returned and the examples (the parameters are listed by the signature). */
export function docMarkdown(doc, declaredReturn = null) {
  const parts = [];
  if (doc.deprecated !== null) parts.push('**Deprecated**' + (doc.deprecated ? ': ' + doc.deprecated : '.'));
  if (doc.summary) parts.push(doc.summary);
  if (doc.returns || doc.returnType) parts.push('**Returns** ' + (doc.returnType ? '`' + doc.returnType + '` ' : '') + (doc.returns ?? '') + (doc.returnType && declaredReturn && doc.returnType.replace(/\s+/g, '').toLowerCase() !== declaredReturn.replace(/\s+/g, '').toLowerCase() ? ` *(declared \`${declaredReturn}\`)*` : ''));
  for (const example of doc.examples) parts.push('**Example**\n```\n' + example + '\n```');
  return parts.join('\n\n');
}

const paramLine = (p, typeOf, note) => `- \`${typeOf(p)} ${p.name}\`` + (p.default !== undefined && p.default !== null ? ` (default \`${p.default}\`)` : '') + (note ? ' — ' + note : '');

/**
 * The hover of a call or a name, as markdown, like the language server gives it: a helper of the song (signature, doc comment, parameters with their notes, returns, kind) or a built-in operator
 * (signature, what it does, parameters with their notes, "Built in."). `helpers` is the signatures() of the helper table, `docs` the docsOf() of the song, `catalog` NOTE_SIGNATURES. Null when the word is neither.
 */
export function hoverText(wordText, afterWord, { helpers = {}, docs = new Map(), catalog = {}, builtins = {}, before = '', insideBlock = false, inferred = new Map() } = {}) {
  const key = Object.keys(helpers).find((n) => n.toLowerCase() === wordText.toLowerCase());
  if (key) {
    const sig = helpers[key], doc = docs.get(sig.line);
    const text = (p) => p.type + ' ' + p.name + (p.default !== undefined ? ' = ' + p.default : '');
    const worked = sig.returnsDeclared === false ? inferred.get(sig.id.toLowerCase()) ?? null : null;   // a helper that does not say what it returns: what its body returns
    const returnType = sig.returnsDeclared === false ? worked : sig.returns;
    const head = `\`${sig.id}(${sig.params.map(text).join(', ')})${returnType ? ' -> ' + returnType : ''}\``;
    const kind = sig.effect === 'dynamic' ? 'dynamic helper: its result can differ between builds until it is baked' : 'helper (pure)';
    const params = sig.params.length ? '\n\n' + sig.params.map((p) => { const d = doc?.params.find((x) => x.name.toLowerCase() === p.name.toLowerCase()); const other = d?.type && d.type.replace(/\s+/g, '').toLowerCase() !== p.type.replace(/\s+/g, '').toLowerCase() ? ` *(documented as \`${d.type}\`)*` : ''; return paramLine(p, (q) => q.type, (d?.text ?? '') + other); }).join('\n') : '';
    const described = doc ? docMarkdown(doc, sig.returnsDeclared === false ? null : sig.returns) : '';
    const returns = doc?.returns || doc?.returnType || !returnType ? '' : worked ? `\n\nReturns \`${worked}\` — worked out from what the body returns.` : `\n\nReturns \`${sig.returns}\`.`;
    return head + (described ? '\n\n' + described : '') + params + returns + `\n\n${kind}`;
  }
  const op = catalog[wordText];
  if (op && String(afterWord).trimStart().startsWith('(')) {
    return signatureMarkdown(op, op.receiver ? `${op.receiver} |> ` : '');
  }
  // the other built-ins of the language layer: functions (Round, min ...), methods (seed.fork), units (2bars) and the words of a helper body (for, emit ...)
  const builtin = builtins[wordText];
  if (builtin) {
    const calling = String(afterWord).trimStart().startsWith('(');
    if (builtin.kind === 'function' && calling) return signatureMarkdown(builtin, '');
    if (builtin.kind === 'method' && calling && before.endsWith('.')) return signatureMarkdown(builtin, builtin.receiver ? builtin.receiver + '.' : '');
    if (builtin.kind === 'unit' && /[0-9.]$/.test(before)) return `\`${builtin.id}\` — unit\n\n${builtin.doc}\n\nBuilt in.`;
    const lead = before.trimEnd();
    if (builtin.kind === 'keyword' && !insideBlock && (lead === '' || /[{;]$/.test(lead) || lead.endsWith('dynamic'))) return `\`${builtin.id}\` — keyword\n\n${builtin.doc}\n\nBuilt in.`;
  }
  return null;
}

function signatureMarkdown(op, receiver) {
  const typeOf = (p) => (p.type === 'Range' && p.of ? `Range<${p.of}>` : p.type);
  const params = op.params?.length ? '\n\n' + op.params.map((p) => paramLine(p, typeOf, p.doc)).join('\n') : '';
  return `\`${receiver}${op.id}(${(op.params ?? []).map((p) => typeOf(p) + ' ' + p.name + (p.default ? ' = ' + p.default : '')).join(', ')}) -> ${op.returns}\`` + (op.doc ? '\n\n' + op.doc : '') + params + '\n\nBuilt in.';
}

/** The markdown of a hover as blocks for a tooltip: { type: 'p' | 'list' | 'code', ... }, with inline pieces { text, code?, bold? }. */
export function markdownBlocks(markdown) {
  const inline = (text) => {
    const parts = [];
    for (const piece of String(text).split(/(`[^`]+`|\*\*[^*]+\*\*)/).filter(Boolean)) {
      if (piece.startsWith('`')) parts.push({ text: piece.slice(1, -1), code: true });
      else if (piece.startsWith('**')) parts.push({ text: piece.slice(2, -2), bold: true });
      else parts.push({ text: piece });
    }
    return parts;
  };
  const blocks = [];
  const lines = String(markdown).split('\n');
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    if (line.startsWith('```')) { const code = []; for (i++; i < lines.length && !lines[i].startsWith('```'); i++) code.push(lines[i]); blocks.push({ type: 'code', text: code.join('\n') }); continue; }
    if (line.startsWith('- ')) { const items = []; for (; i < lines.length && lines[i].startsWith('- '); i++) items.push(inline(lines[i].slice(2))); i--; blocks.push({ type: 'list', items }); continue; }
    if (line.trim() === '') continue;
    const paragraph = [line];
    while (i + 1 < lines.length && lines[i + 1].trim() !== '' && !lines[i + 1].startsWith('```') && !lines[i + 1].startsWith('- ')) paragraph.push(lines[++i]);
    blocks.push({ type: 'p', parts: inline(paragraph.join('\n')) });
  }
  return blocks;
}

const sameType = (a, b, table) => {
  const alias = (name) => table.type(name);
  const x = canonicalType(a, alias), y = canonicalType(b, alias);
  return x.size === y.size && [...x].every((m) => y.has(m));
};
const knownType = (member, table) => {
  const name = member.replace(/\?$/, '');
  return name.endsWith('[]') || !!NOTE_TYPES[name] || BUILTIN_TYPES.has(name) || !!table.type(name) || ['Value', 'None', 'Null', 'Any'].includes(name);
};

/**
 * What the doc comments say about the helpers against what the declarations say (AMP2090 to AMP2094), and the calls of a deprecated helper (AMP2093): the browser twin of DocDiagnostics in Parser.cs.
 * `lines` are the original lines of the song, `blankedLines` the same without block comments, `table` the helper table. Diagnostics are in the shape of the language server (0-based lines).
 */
export function docDiagnostics(lines, blankedLines, table) {
  const docs = docsOf(lines), out = [];
  const at = (line, name) => { const i = Math.max(0, (lines[line] ?? '').indexOf(name)); return { start: { line, character: i }, end: { line, character: i + name.length } }; };
  const add = (line, name, code, severity, message) => out.push({ range: at(line, name), severity, code, source: 'ample (browser)', message });
  for (const sig of Object.values(table.signatures())) {
    const doc = docs.get(sig.line);
    if (!doc) continue;
    const names = sig.params.map((p) => p.name);
    for (const p of doc.params) {
      const declared = sig.params.find((d) => d.name.toLowerCase() === p.name.toLowerCase());
      if (!declared) { add(sig.line, sig.id, 'AMP2090', 2, `The doc comment of "${sig.id}" describes a parameter "${p.name}" that it does not have (${names.length ? 'it has ' + names.join(', ') : 'it has none'}).`); continue; }
      if (p.type && !sameType(p.type, declared.type, table)) add(sig.line, sig.id, 'AMP2091', 2, `The doc comment says "${p.name}" is ${p.type}, the declaration says ${declared.type}.`);
    }
    const declaredReturn = table.helper(sig.id)?.node.returns;   // only a return type that is written counts (the signature falls back to Notes)
    if (doc.returnType && declaredReturn && !sameType(doc.returnType, declaredReturn, table)) add(sig.line, sig.id, 'AMP2091', 2, `The doc comment says "${sig.id}" returns ${doc.returnType}, the declaration says ${declaredReturn}.`);
    const types = [...doc.params.map((p) => p.type), doc.returnType].filter(Boolean).flatMap((t) => typeNames(t));
    for (const member of new Set(types)) if (!knownType(member, table)) add(sig.line, sig.id, 'AMP2092', 2, `The doc comment of "${sig.id}" uses a type that does not exist: ${member}.`);
    if (doc.params.length) {
      const missing = names.filter((n) => !doc.params.some((p) => p.name.toLowerCase() === n.toLowerCase()));
      if (missing.length) add(sig.line, sig.id, 'AMP2094', 3, `The doc comment of "${sig.id}" describes some parameters but not ${missing.join(', ')}.`);
    }
    if (doc.deprecated !== null) {
      const call = new RegExp('(?<![\\w-])' + sig.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\s*\\(', 'i');
      blankedLines.forEach((text, k) => {
        if (k === sig.line) return;
        const code = text.includes('#') ? text.slice(0, text.indexOf('#')) : text;
        if (call.test(code) && !/^\s*(?:dynamic\s+)?helper\b/.test(code)) add(k, sig.id, 'AMP2093', 2, `"${sig.id}" is deprecated${doc.deprecated ? ': ' + doc.deprecated : '.'}`);
      });
    }
  }
  return out;
}
