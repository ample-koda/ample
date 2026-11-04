// The templates of a project (plan templates-library T06): a piece of song source (blocks and the links between them) with a few settings, kept as a JSON file in the project's templates folder. Pure functions to take
// blocks out of a song, make some of their values settings, and put the blocks into another song under free names and at another bar. @ample:templates

import { HEADER_TAIL } from './block-header.mjs';

const HEADER = new RegExp(String.raw`^([A-Za-z][\w-]*)\s+([A-Za-z][\w-]*)` + HEADER_TAIL);
const FROM = /^\s*from\s+([A-Za-z][\w-]*)\.([\w-]+)\s*->\s*([\w-]+)/;
const count = (line, ch) => line.split(ch).length - 1;

/** Where the blocks of a song are: [{ type, name, start, end }] with the 0-based first and last line of each (a block ends at the line where its braces are closed). */
export function blockRanges(text) {
  const lines = String(text ?? '').split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    const header = !/^\s/.test(lines[i]) ? HEADER.exec(lines[i]) : null;
    if (!header) continue;
    let depth = count(lines[i], '{') - count(lines[i], '}');
    let end = i;
    while (depth > 0 && end + 1 < lines.length) { end++; depth += count(lines[end], '{') - count(lines[end], '}'); }
    out.push({ type: header[1], name: header[2], start: i, end });
    i = end;
  }
  return out;
}

/**
 * The source of some blocks of a song, in the order of the song. A link to a block that is not taken along cannot work in another song: its line is left out and listed in `external`.
 * Returns { source, names, external: [{ block, from }] }.
 */
export function extractBlocks(text, names) {
  const wanted = new Set(names);
  const lines = String(text ?? '').split('\n');
  const parts = [];
  const external = [];
  const found = [];
  for (const range of blockRanges(text)) {
    if (!wanted.has(range.name)) continue;
    found.push(range.name);
    const kept = [];
    for (let i = range.start; i <= range.end; i++) {
      const from = FROM.exec(lines[i]);
      if (from && !wanted.has(from[1])) { external.push({ block: range.name, from: from[1] }); continue; }
      kept.push(lines[i]);
    }
    parts.push(kept.join('\n'));
  }
  return { source: parts.join('\n\n'), names: found, external };
}

/** Which values of the blocks could become settings: [{ block, param, value }] from the lines "  param value" inside each block. */
export function adjustableValues(source) {
  const lines = String(source ?? '').split('\n');
  const out = [];
  for (const range of blockRanges(source)) {
    for (let i = range.start + 1; i < range.end; i++) {
      const m = /^\s+([a-z_][\w-]*)\s+(\S.*)$/i.exec(lines[i]);
      if (m && !/^(from|notes|note|melody)\b/.test(m[1]) && !m[2].includes('{')) out.push({ block: range.name, param: m[1], value: m[2].trim() });
    }
  }
  return out;
}

/** The blocks of some source and whether each is on now (a block is off when its header has the flag disabled): [{ block, on }]. Only blocks whose header ends with an opening brace can be switched. */
export function switchableBlocks(source) {
  const lines = String(source ?? '').split('\n');
  return blockRanges(source).filter((r) => /\{\s*$/.test(lines[r.start])).map((r) => ({ block: r.name, on: !/\sdisabled\b/.test(lines[r.start].replace(/\{\s*$/, '')) }));
}

/** Makes values settings: each pick { block, param, key, label } puts {{key}} in the place of the value and gives the setting its old value as default. Returns { source, settings }. */
export function parametrize(source, picks, switches = []) {
  const lines = String(source ?? '').split('\n');
  const ranges = blockRanges(source);
  const settings = [];
  for (const pick of picks) {
    const range = ranges.find((r) => r.name === pick.block);
    if (!range) continue;
    for (let i = range.start + 1; i < range.end; i++) {
      const m = /^(\s+)([a-z_][\w-]*)(\s+)(\S.*)$/i.exec(lines[i]);
      if (m && m[2] === pick.param) {
        settings.push({ name: pick.key, label: pick.label || pick.key, default: m[4].trim() });
        lines[i] = `${m[1]}${m[2]}${m[3]}{{${pick.key}}}`;
        break;
      }
    }
  }
  // a switch puts the flag disabled under a setting: {{key}} is " disabled" when the block is off and nothing when it is on
  for (const sw of switches) {
    const range = ranges.find((r) => r.name === sw.block);
    if (!range || !/\{\s*$/.test(lines[range.start])) continue;
    const head = lines[range.start].replace(/\{\s*$/, '');
    const wasOff = /\sdisabled\b/.test(head);
    lines[range.start] = head.replace(/\s+disabled\b/, '').replace(/\s+$/, '') + '{{' + sw.key + '}} {';
    settings.push({ name: sw.key, label: sw.label || sw.block, kind: 'switch', off: ' disabled', default: wasOff ? ' disabled' : '' });
  }
  return { source: lines.join('\n'), settings };
}

/** The source with the settings filled in (a setting without a value keeps its default). */
export function fill(source, settings, values = {}) {
  return String(source ?? '').replace(/\{\{([\w-]+)\}\}/g, (all, key) => {
    const s = settings.find((x) => x.name === key);
    return String(values[key] ?? s?.default ?? '');
  });
}

/**
 * Every bar of the blocks moves by an offset: "at 1 for 8" with 4 becomes "at 5 for 8", also in a header with several parts. The bars of an automation move with it
 * (they are bars of the song): "auto(1:-8, 9:-5)" with 4 becomes "auto(5:-8, 13:-5)"; points in seconds ("0.5s:-60") stay.
 */
export function shiftBars(source, offset) {
  if (!offset) return String(source ?? '');
  const bar = (n) => String(Math.round((Number(n) + offset) * 1e6) / 1e6);
  return String(source ?? '').split('\n').map((line) => {
    if (HEADER.test(line) && !/^\s/.test(line)) return line.replace(/(\bat\s+|,\s*)(\d+)(?=\s+for\b)/g, (all, p, n) => p + (Number(n) + offset));
    return line.replace(/\bauto\(([^)]*)\)/g, (all, inner) => 'auto(' + inner.replace(/(^|,\s*)(\d+(?:\.\d+)?)(?=\s*:)/g, (m, p, n) => p + bar(n)) + ')');
  }).join('\n');
}

/** The blocks under names that are free in a song: a name that is taken gets -2, -3 and so on, and the links inside the source follow. Returns { source, map }. */
export function renameBlocks(source, taken) {
  const used = new Set(taken);
  const map = {};
  for (const r of blockRanges(source)) {
    let name = r.name;
    for (let i = 2; used.has(name); i++) name = `${r.name}-${i}`;
    used.add(name);
    map[r.name] = name;
  }
  const lines = String(source ?? '').split('\n');
  for (const r of blockRanges(source)) {
    if (map[r.name] !== r.name) lines[r.start] = lines[r.start].replace(new RegExp(`^(\\s*[A-Za-z][\\w-]*\\s+)${r.name}\\b`), `$1${map[r.name]}`);
  }
  const done = lines.map((line) => {
    const m = FROM.exec(line);
    if (!m || map[m[1]] === undefined || map[m[1]] === m[1]) return line;
    return line.replace(new RegExp(`\\bfrom\\s+${m[1]}\\.`), `from ${map[m[1]]}.`);
  }).join('\n');
  return { source: done, map };
}

/** Whether the source has a master block (a song has one master, so a template with one may be linked to the master of the song). */
export const hasMaster = (source) => blockRanges(source).some((r) => r.type === 'master');

/**
 * Takes the master out of the template source and feeds what it was fed with into the master that the song already has: into a free input of the mixer in front of that master, or (no mixer, or no free input)
 * into a new mixer that is put in front of it. Returns { songText, source, mixer } (mixer: the name of a new mixer, or ''); both texts stay the same when one of them has no master.
 */
export function mergeMaster(songText, source) {
  const own = blockRanges(source).filter((r) => r.type === 'master');
  const mine = blockRanges(songText).find((r) => r.type === 'master');
  if (!own.length || !mine) return { songText, source, mixer: '' };
  const lines = String(source).split('\n');
  const feeds = [];
  for (const r of own) for (let i = r.start; i <= r.end; i++) { const m = /^\s*from\s+([\w-]+)\.([\w-]+)\s*->\s*mix\b/.exec(lines[i]); if (m && !/\sdisabled\s*$/.test(lines[i])) feeds.push(`${m[1]}.${m[2]}`); }
  for (const r of [...own].reverse()) lines.splice(r.start, r.end - r.start + 1);
  const rest = lines.join('\n').replace(/\n{3,}/g, '\n\n').replace(/^\s+|\s+$/g, '');
  if (!feeds.length) return { songText, source: rest, mixer: '' };
  const song = String(songText).split('\n');
  let feedLine = -1, old = '';
  for (let i = mine.start; i <= mine.end; i++) { const m = /^\s*from\s+([\w-]+\.[\w-]+)\s*->\s*mix\b/.exec(song[i]); if (m) { feedLine = i; old = m[1]; break; } }
  const mixerBlock = old ? blockRanges(songText).find((r) => r.type === 'mixer' && r.name === old.split('.')[0]) : null;
  if (mixerBlock) {
    const used = new Set();
    for (let i = mixerBlock.start; i <= mixerBlock.end; i++) for (const m of song[i].matchAll(/->\s*in(\d+)\b/g)) used.add(Number(m[1]));
    const free = []; for (let n = 1; n <= 10; n++) if (!used.has(n)) free.push(n);
    if (free.length >= feeds.length) {
      song.splice(mixerBlock.end, 0, ...feeds.map((f, i) => `  from ${f} -> in${free[i]}`));
      return { songText: song.join('\n'), source: rest, mixer: '' };
    }
  }
  const taken = new Set([...blockRanges(songText), ...blockRanges(rest)].map((r) => r.name));
  let mixer = `${mine.name}-mix`;
  for (let i = 2; taken.has(mixer); i++) mixer = `${mine.name}-mix-${i}`;
  const ins = [...(old ? [old] : []), ...feeds].slice(0, 10).map((f, i) => `  from ${f} -> in${i + 1}`);
  const block = `mixer ${mixer} {\n${ins.join('\n')}\n}`;
  if (feedLine >= 0) song[feedLine] = song[feedLine].replace(/from\s+[\w-]+\.[\w-]+/, `from ${mixer}.mix`);
  else song.splice(mine.end, 0, `  from ${mixer}.mix -> mix`);
  return { songText: appendSource(song.join('\n'), block), source: rest, mixer };
}

/** The song text with the blocks added at the end, a blank line between. */
export const appendSource = (text, source) => `${String(text ?? '').replace(/\s*$/, '')}\n\n${String(source ?? '').replace(/^\s+|\s+$/g, '')}\n`;

export const templateId = (name) => String(name ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'template';

/** A template as it is kept in the file. */
export const makeTemplate = ({ name, say = '', tags = [], source, settings = [] }) => ({ format: 1, id: templateId(name), name: String(name).trim(), say, tags, source, settings });

/** What is wrong with a template read from a file ([] when it is fine). */
export function templateProblems(t) {
  const out = [];
  if (!t || typeof t !== 'object') return ['not an object'];
  if (t.format !== 1) out.push('format must be 1');
  if (!/^[a-z0-9-]+$/.test(String(t.id ?? ''))) out.push('id must be letters, digits and -');
  if (!String(t.name ?? '').trim()) out.push('name is empty');
  if (!String(t.source ?? '').trim()) out.push('source is empty');
  if (!Array.isArray(t.settings ?? [])) out.push('settings must be a list');
  for (const s of t.settings ?? []) if (!s || !/^[\w-]+$/.test(String(s.name ?? ''))) out.push(`setting without a name: ${JSON.stringify(s)}`);
  const used = new Set([...String(t.source ?? '').matchAll(/\{\{([\w-]+)\}\}/g)].map((m) => m[1]));
  for (const key of used) if (!(t.settings ?? []).some((s) => s.name === key)) out.push(`{{${key}}} has no setting`);
  return out;
}
