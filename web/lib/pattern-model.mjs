// The pure part of the pattern editors (plan patterns P02): the named patterns and the section sequence of a drums block, read, written and edited as text.
// Twin of DrumSong in src/Ample.Audio/Engine/DrumSong.cs (the same grammar and the same names). @ample:patterns

/** The word that stands for the plain `pattern` parameter of the block. */
export const MAIN = 'pattern';

/** The names the engine knows without a definition. */
export const BUILT_IN = ['beat-A', 'beat-B'];

export const NAME = /^[A-Za-z0-9_-]+$/;
const unquote = (text) => String(text ?? '').trim().replace(/^"|"$/g, '').trim();

/** Reads `verse=kick:x... snare:.... / fill=kick:xxxx` into [{ name, grid }]; lines it cannot read are dropped and reported in `problems`. */
export function parsePatterns(text) {
  const patterns = [], problems = [];
  const body = unquote(text);
  if (!body) return { patterns, problems };
  for (const part of body.split('/').map((p) => p.trim()).filter(Boolean)) {
    const eq = part.indexOf('=');
    const name = eq > 0 ? part.slice(0, eq).trim() : '';
    const grid = eq > 0 ? part.slice(eq + 1).trim() : '';
    if (!NAME.test(name) || !grid) { problems.push(`'${part}' is not name=grids`); continue; }
    if (name === MAIN || BUILT_IN.includes(name)) { problems.push(`'${name}' is taken by the engine`); continue; }
    if (patterns.some((p) => p.name === name)) { problems.push(`'${name}' is defined twice`); continue; }
    patterns.push({ name, grid });
  }
  return { patterns, problems };
}

export const formatPatterns = (patterns) => patterns.map((p) => `${p.name}=${p.grid}`).join(' / ');

/** Reads `verse*4 fill chorus*8` into [{ name, bars }]. */
export function parseSong(text) {
  const entries = [], problems = [];
  for (const token of unquote(text).split(/[\s,]+/).filter(Boolean)) {
    const star = token.indexOf('*');
    const name = star < 0 ? token : token.slice(0, star);
    const bars = star < 0 ? 1 : Number(token.slice(star + 1));
    if (!NAME.test(name) || !Number.isInteger(bars) || bars < 1 || bars > 999) { problems.push(`'${token}' is not name or name*bars`); continue; }
    entries.push({ name, bars });
  }
  return { entries, problems };
}

export const formatSong = (entries) => entries.map((e) => (e.bars > 1 ? `${e.name}*${e.bars}` : e.name)).join(' ');

/** One pattern name per bar. */
export const expandSong = (entries) => entries.flatMap((e) => Array.from({ length: e.bars }, () => e.name));

/** The pattern name of a bar counted from the start of the block (0 is the first bar); the sequence repeats. No song: the plain pattern. */
export function nameAtBar(entries, barIndex) {
  const expanded = expandSong(entries);
  return expanded.length ? expanded[((barIndex % expanded.length) + expanded.length) % expanded.length] : MAIN;
}

/** The sections with the bar each one starts at (1 is the first bar of the block), for drawing the strip. */
export function sectionRanges(entries) {
  let start = 1;
  return entries.map((e, index) => { const range = { index, name: e.name, bars: e.bars, start }; start += e.bars; return range; });
}

/** A name that is not taken yet: base, base-2, base-3... */
export function uniqueName(base, taken) {
  const clean = String(base || 'pattern').replace(/[^A-Za-z0-9_-]+/g, '-').replace(/^-+|-+$/g, '') || 'pattern';
  const used = new Set([...taken, MAIN, ...BUILT_IN]);
  if (!used.has(clean)) return clean;
  for (let i = 2; ; i++) if (!used.has(`${clean}-${i}`)) return `${clean}-${i}`;
}

/** The names a section can point at: the plain pattern, the defined ones and the built-in ones. */
export const sectionNames = (patterns) => [MAIN, ...patterns.map((p) => p.name), ...BUILT_IN];

/** Problems that would stop the engine: a section that names no pattern. */
export function check(patterns, entries) {
  const known = new Set(sectionNames(patterns));
  return entries.filter((e) => !known.has(e.name)).map((e) => `the song names '${e.name}', which is not a pattern`);
}

// --- edits: each returns the new { patterns, entries } and leaves its arguments alone ---

export function addPattern(patterns, entries, name, grid) {
  const unique = uniqueName(name, patterns.map((p) => p.name));
  return { patterns: [...patterns, { name: unique, grid }], entries, name: unique };
}

export function duplicatePattern(patterns, entries, from, grid) {
  return addPattern(patterns, entries, `${from}-copy`, grid);
}

export function renamePattern(patterns, entries, from, to) {
  if (!NAME.test(to) || to === MAIN || BUILT_IN.includes(to) || patterns.some((p) => p.name === to)) return null;
  return { patterns: patterns.map((p) => (p.name === from ? { ...p, name: to } : p)), entries: entries.map((e) => (e.name === from ? { ...e, name: to } : e)), name: to };
}

export function deletePattern(patterns, entries, name) {
  return { patterns: patterns.filter((p) => p.name !== name), entries: entries.filter((e) => e.name !== name) };
}

export const setGrid = (patterns, name, grid) => patterns.map((p) => (p.name === name ? { ...p, grid } : p));

export function addSection(entries, name, bars = 1, at = entries.length) {
  const list = [...entries];
  list.splice(Math.max(0, Math.min(list.length, at)), 0, { name, bars: Math.max(1, Math.min(999, Math.round(bars))) });
  return list;
}

export const removeSection = (entries, index) => entries.filter((_, i) => i !== index);

export function moveSection(entries, from, to) {
  if (from < 0 || from >= entries.length) return entries;
  const list = [...entries];
  const [moved] = list.splice(from, 1);
  list.splice(Math.max(0, Math.min(list.length, to)), 0, moved);
  return list;
}

export const setBars = (entries, index, bars) => entries.map((e, i) => (i === index ? { ...e, bars: Math.max(1, Math.min(999, Math.round(bars))) } : e));
