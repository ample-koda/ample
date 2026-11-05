// What the canvas keeps of the owner's own arrangement besides the blocks: the groups, the size of blocks and the pinned blocks. It is written in the song file as a
// `view { ... }` block at the end (the language checks it, the song does not depend on it), so it goes along with the song (backup, history, git, another computer).
// Songs from before kept the same in comment lines (`# view group {json}`, `# view size <block> <scale>`, `# view pin <block>`): those are still read, and the next
// save writes the block instead (the same upgrade the editor offers as a quick fix, src/Ample.Language/ViewBlock.cs). Pure, tested without a DOM.
// The block also holds what the Playlist remembers: `section "Drop" at 33 for 32`, `line kick "Kick stack"` (a name for the line of a block) and
// `order kick snare` (the order of the lines). Whatever else is on a line of the block that this file cannot read (a newer editor's line, a comment) is kept
// as it is by writeView, so a save never drops it. @ample:view-lines

const GROUP = '# view group ';
const SIZE = '# view size ';
const PIN = '# view pin ';
const WORDS = /"(?:[^"\\]|\\.)*"|\S+/g;
const BARE = /^[A-Za-z0-9_.-]+$/;
const KEY = /^[A-Za-z][A-Za-z0-9_-]*$/;

const words = (row) => row.match(WORDS) ?? [];
const unword = (w) => (w.startsWith('"') ? JSON.parse(w) : w);
const word = (s) => (BARE.test(s) ? s : JSON.stringify(s));
const restAfter = (row, n) => { let rest = row.trim(); for (let i = 0; i < n; i++) rest = rest.slice(words(rest)[0].length).trim(); return rest; };

/** Reads the view of a song text (the block, and the older comment lines): { found, groups, sizes, pins, sections, lineNames, order, unread }. `unread` holds the lines of the block that are not understood, to be kept. */
export function parseView(text) {
  const groups = {}, sizes = {}, pins = [], sections = [], lineNames = {}, unread = [];
  let order = [];
  let found = false, inView = false, group = null;
  for (const line of String(text || '').split('\n')) {
    const row = line.replace(/\r$/, '').trim();
    if (!inView && row === 'view {') { inView = true; continue; }
    if (inView) {
      try {
        if (row === '}') {
          if (group) { if (typeof group.id === 'string') { groups[group.id] = group; found = true; } group = null; } else { inView = false; found = true; }
        } else if (!row) {
          // blank
        } else if (row.startsWith('#')) {
          if (!group) unread.push(row);
        } else if (group) {
          const [key, value] = words(row);
          const rest = restAfter(row, 1);
          if (key === 'member') group.members.push(unword(value));
          else if (key === 'label') { const label = words(rest); (group.labels ??= {})[unword(label[0])] = JSON.parse(restAfter(rest, 1)); }
          else if (KEY.test(key)) group[key] = JSON.parse(rest);
        } else {
          const w = words(row);
          if (w[0] === 'group' && w[2] === '{') group = { id: unword(w[1]), members: [] };
          else if (w[0] === 'pin' && w[1]) { pins.push(unword(w[1])); found = true; }
          else if (w[0] === 'size' && w.length === 3) { const scale = Number(w[2]); if (Number.isFinite(scale) && scale > 0) { sizes[unword(w[1])] = scale; found = true; } }
          else if (w[0] === 'section' && w.length === 6 && w[2] === 'at' && w[4] === 'for' && Number.isInteger(Number(w[3])) && Number.isInteger(Number(w[5]))) { sections.push({ name: unword(w[1]), at: Number(w[3]), length: Number(w[5]) }); found = true; }
          else if (w[0] === 'line' && w.length === 3) { lineNames[unword(w[1])] = unword(w[2]); found = true; }
          else if (w[0] === 'order' && w.length >= 2) { order = w.slice(1).map(unword); found = true; }
          else unread.push(row);
        }
      } catch { /* a line that was edited by hand */ }
    } else if (row.startsWith(GROUP)) {
      try { const g = JSON.parse(row.slice(GROUP.length)); if (g && typeof g.id === 'string' && Array.isArray(g.members)) { groups[g.id] = g; found = true; } } catch { /* a line that was edited by hand */ }
    } else if (row.startsWith(PIN)) {
      const name = row.slice(PIN.length).trim();
      if (name) { pins.push(name); found = true; }
    } else if (row.startsWith(SIZE)) {
      const rest = row.slice(SIZE.length), cut = rest.lastIndexOf(' ');
      const scale = Number(rest.slice(cut + 1));
      if (cut > 0 && Number.isFinite(scale) && scale > 0) { sizes[rest.slice(0, cut)] = scale; found = true; }
    }
  }
  return { found, groups, sizes, pins, sections, lineNames, order, unread };
}

/** The lines of one group inside the block: title, one member per line, the other fields as `name json`, the labels as `label <key> <text>`. */
function groupLines(g) {
  const lines = [`  group ${word(g.id)} {`];
  if (typeof g.title === 'string') lines.push(`    title ${JSON.stringify(g.title)}`);
  for (const m of g.members || []) lines.push(`    member ${word(String(m))}`);
  for (const [key, value] of Object.entries(g)) if (!['id', 'title', 'members', 'labels'].includes(key) && KEY.test(key) && value !== undefined) lines.push(`    ${key} ${JSON.stringify(value)}`);
  for (const [key, value] of Object.entries(g.labels || {})) lines.push(`    label ${JSON.stringify(key)} ${JSON.stringify(value)}`);
  lines.push('  }');
  return lines;
}

/** The song text without its view: the block and the older comment lines. */
function withoutView(text) {
  const kept = [];
  let depth = 0;
  for (const line of String(text || '').split('\n')) {
    const row = line.trim();
    if (depth > 0) { depth += row.endsWith('{') ? 1 : row === '}' ? -1 : 0; continue; }
    if (row === 'view {') { depth = 1; continue; }
    if (!row.startsWith('# view ') && !row.startsWith('# view:')) kept.push(line);
  }
  return kept;
}

/** The song text with its view replaced by these groups, sizes and pins (no block at all when there is nothing to keep). `more` is { sections, lineNames, order, unread } as parseView gives them. */
export function writeView(text, groups, sizes, pins = [], more = {}) {
  const kept = withoutView(text);
  while (kept.length && kept[kept.length - 1].trim() === '') kept.pop();
  const lines = [
    ...[...(more.sections || [])].sort((a, b) => a.at - b.at || a.length - b.length).map((s) => `  section ${JSON.stringify(s.name)} at ${s.at} for ${s.length}`),
    ...Object.entries(more.lineNames || {}).map(([block, name]) => `  line ${word(block)} ${JSON.stringify(name)}`),
    ...((more.order || []).length ? [`  order ${more.order.map(word).join(' ')}`] : []),
    ...Object.values(groups || {}).flatMap(groupLines),
    ...Object.entries(sizes || {}).filter(([, scale]) => scale && scale !== 1).map(([id, scale]) => `  size ${word(id)} ${scale}`),
    ...[...(pins || [])].sort().map((name) => `  pin ${word(name)}`),
    ...(more.unread || []).map((row) => `  ${row}`),
  ];
  if (!lines.length) return `${kept.join('\n')}\n`;
  return `${kept.join('\n')}\n\nview {\n${lines.join('\n')}\n}\n`;
}

/** The song text without its view: two texts that are equal here differ only in what the editors remember, never in what the song sounds like. */
export const withoutViewText = (text) => withoutView(text).join('\n').trimEnd();
