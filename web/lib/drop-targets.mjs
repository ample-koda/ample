// Where a file can be dropped in the text of a song, and the edit a drop makes (plan project-panel PP03, PP04).
// The code editor lights the targets while a file is dragged; the same edit is used by the menu entries and the keyboard command. Pure: text in, text out. @ample:drop-targets

import { blockText, blocksFor, formatValue, freeBlockName, kindOf, paramAccepts } from './file-kinds.mjs';

import { HEADER_TAIL } from './block-header.mjs';

const HEADER = new RegExp(String.raw`^([A-Za-z][\w-]*)\s+([A-Za-z0-9_/-]+)` + HEADER_TAIL + String.raw`[^{]*\{\s*$`);
const USE = /^use\s+([A-Za-z0-9_-]+)(?:@\S+)?\s+as\s+([A-Za-z0-9_-]+)\s*\{\s*$/;
const PARAM = /^(\s+)([A-Za-z][\w-]*)\s+(\S.*?)\s*$/;

/** The blocks of a song with their header, body and parameter lines (0-based line numbers). Text it does not understand is skipped. */
export function scanBlocks(text) {
  const lines = String(text).split(/\r?\n/);
  const blocks = [];
  for (let i = 0; i < lines.length; i++) {
    const h = HEADER.exec(lines[i]) || null;
    const u = h ? null : USE.exec(lines[i]);
    if (!h && !u) continue;
    let end = i + 1;
    while (end < lines.length && !/^\}/.test(lines[end])) end++;
    if (end >= lines.length) break;   // not closed: leave it to the diagnostics
    const params = [];
    for (let j = i + 1; j < end; j++) {
      const m = PARAM.exec(lines[j]);
      if (!m || m[2] === 'from') continue;
      const valueStart = lines[j].indexOf(m[3], m[1].length + m[2].length);
      params.push({ name: m[2], value: m[3], line: j, valueStart, valueEnd: valueStart + m[3].length });
    }
    blocks.push({ type: h ? h[1] : 'use', name: h ? h[2] : u[2], line: i, end, params });
    i = end;
  }
  return { lines, blocks };
}

/**
 * The places where a file of this kind can be dropped. templates: S.templates. Returns { fields, inserts }:
 * fields are parameters that take the kind (`add: true` when the block has no such line yet and one is added), inserts are the lines before which a new block goes.
 */
export function dropTargets(text, templates, kind) {
  const { lines, blocks } = scanBlocks(text);
  const byType = new Map((templates || []).map((t) => [t.id, t]));
  const fields = [], inserts = [];
  for (const b of blocks) {
    const t = byType.get(b.type);
    if (!t) continue;
    for (const p of t.parameters || []) {
      if (!paramAccepts(p, kind)) continue;
      const has = b.params.find((x) => x.name === p.name);
      fields.push(has ? { block: b.name, blockType: b.type, param: p.name, line: has.line, valueStart: has.valueStart, valueEnd: has.valueEnd, add: false } : { block: b.name, blockType: b.type, param: p.name, line: b.end, add: true });
    }
  }
  if (kind && (kind.id === 'module' || blocksFor(kind, templates).length > 0)) {
    const project = lines.findIndex((l) => /^(?:project|composition)\s/.test(l));
    if (project >= 0) inserts.push({ line: project + 1, after: 'project' });
    for (const b of blocks) inserts.push({ line: b.end + 1, after: b.name });
    if (!inserts.length) inserts.push({ line: lines.length, after: null });
  }
  return { fields, inserts };
}

/** The target nearest to a line (a field on that line wins, then the closest insert place within `reach` lines). */
export function nearestTarget(targets, line, reach = 2) {
  const field = targets.fields.find((f) => f.line === line);
  if (field) return { type: 'field', ...field };
  let best = null;
  for (const t of targets.inserts) {
    const d = Math.abs(t.line - line);
    if (d <= reach && (!best || d < best.d)) best = { d, t };
  }
  const nearField = targets.fields.filter((f) => Math.abs(f.line - line) <= 1).sort((a, b) => Math.abs(a.line - line) - Math.abs(b.line - line))[0];
  if (nearField && (!best || Math.abs(nearField.line - line) <= best.d)) return { type: 'field', ...nearField };
  return best ? { type: 'insert', ...best.t } : null;
}

/**
 * Applies a drop. target: from `nearestTarget`; path: the project path (relative); templates: S.templates. replace: true to overwrite an existing path.
 * Returns { text, line } (the line to put the caret on) or { error }. A field that already has a value is only replaced when `replace` is set.
 */
export function applyDrop(text, target, path, templates, { replace = false, isDir = false } = {}) {
  const kind = kindOf(path, isDir);
  const { lines, blocks } = scanBlocks(text);
  const crlf = /\r\n/.test(text);
  const join = (l) => l.join(crlf ? '\r\n' : '\n');
  if (!kind) return { error: 'This kind of file cannot be used in a song.' };
  if (target.type === 'field') {
    if (target.add) {
      lines.splice(target.line, 0, `  ${target.param} ${formatValue(path)}`);
      return { text: join(lines), line: target.line };
    }
    const current = lines[target.line];
    const existing = current.slice(target.valueStart, target.valueEnd).replace(/^"|"$/g, '');
    if (existing && existing !== path && !replace) return { needsConfirm: true, existing };
    lines[target.line] = current.slice(0, target.valueStart) + formatValue(path) + current.slice(target.valueEnd);
    return { text: join(lines), line: target.line };
  }
  if (target.type === 'insert') {
    const names = blocks.map((b) => b.name);
    const choice = blocksFor(kind, templates)[0];
    const template = choice ? (templates || []).find((t) => t.id === choice.block) : null;
    const name = freeBlockName(path, names);
    const body = blockText(kind, path, { template, name, instance: name });
    if (!body) return { error: 'There is no block for this kind of file.' };
    const at = Math.min(target.line, lines.length);
    const insert = body.replace(/\n$/, '').split('\n');
    // a blank line keeps blocks apart
    const before = at > 0 && lines[at - 1].trim() !== '' ? [''] : [];
    lines.splice(at, 0, ...before, ...insert);
    return { text: join(lines), line: at + before.length };
  }
  return { error: 'Drop it on a file line or between two blocks.' };
}
