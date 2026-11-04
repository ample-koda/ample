// The difference between two texts of a song, kept to the structure of the song: a block that is new or gone is one change that starts and ends on the lines of that block, and a block that exists in both is compared
// line by line inside itself. A plain line diff may cut a new block in two (its closing brace matched with the brace of the block before it); this does not. Pure. @ample:koda
import { applyHunks, lineHunks } from './koda-lines.mjs';
import { blockRanges } from './template-model.mjs';

/** The song as segments in order: a block (with its name and lines) or a single line outside any block. */
function segments(text) {
  const lines = String(text ?? '').split('\n');
  const out = [];
  const blocks = blockRanges(text);
  let i = 0;
  while (i < lines.length) {
    const block = blocks.find((b) => b.start === i);
    if (block) { out.push({ kind: 'block', name: block.name, type: block.type, start: i, end: block.end, text: lines.slice(i, block.end + 1).join('\n') }); i = block.end + 1; }
    else { out.push({ kind: 'line', name: lines[i], start: i, end: i, text: lines[i] }); i++; }
  }
  return out;
}

const same = (a, b) => a.kind === b.kind && a.name === b.name;

/**
 * Hunks [{ start, removed, added, kind, block }]: start is the 0-based line of the current text; kind is 'block-added' (added holds the lines of the block, block its name and type), 'block-removed'
 * or 'lines' (a change inside the block named block, or in the lines between blocks).
 */
export function structuredHunks(current, proposed) {
  const a = segments(current);
  const b = segments(proposed);
  const n = a.length, m = b.length;
  const table = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) table[i][j] = same(a[i], b[j]) ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const hunks = [];
  const at = (i) => (i < n ? a[i].start : String(current ?? '').split('\n').length);
  let i = 0, j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && same(a[i], b[j])) {
      if (a[i].text !== b[j].text) {
        const inner = a[i].kind === 'block' ? a[i].name : '';
        for (const h of lineHunks(a[i].text, b[j].text)) hunks.push({ start: a[i].start + h.start, removed: h.removed, added: h.added, kind: 'lines', block: inner });
      }
      i++; j++; continue;
    }
    if (j < m && (i >= n || table[i][j + 1] >= table[i + 1][j])) {
      hunks.push({ start: at(i), removed: 0, added: b[j].text.split('\n'), kind: b[j].kind === 'block' ? 'block-added' : 'lines', block: b[j].kind === 'block' ? b[j].name : '', type: b[j].type });
      j++;
    } else {
      hunks.push({ start: a[i].start, removed: a[i].end - a[i].start + 1, added: [], kind: a[i].kind === 'block' ? 'block-removed' : 'lines', block: a[i].kind === 'block' ? a[i].name : '', type: a[i].type });
      i++;
    }
  }
  return hunks;
}

/** One line changed into another, in words; a long value (a pattern of notes) is told by its name and its size, not printed. */
function changeText(who, before, after) {
  const text = `${who}: ${before}  →  ${after}`;
  if (text.length <= 110) return text;
  const key = before.split(/\s+/)[0];
  const size = (s) => s.slice(key.length).trim().replace(/^"|"$/g, '').split(/\s+/).filter(Boolean).length;
  return `${who}: ${key} changed (${size(before)} → ${size(after)} items)`;
}

const FROM = /^\s*from\s+([A-Za-z][\w-]*)\.([\w-]+)\s*->\s*([\w-]+)/;

/**
 * What a draft changes, in plain words, from the text before and after: new and removed blocks, a link that now comes from another block, a value that changed.
 * Returns [{ type: 'add' | 'remove' | 'link' | 'change', text }], at most eight items.
 */
export function describeChanges(current, proposed) {
  const cur = String(current ?? '').split('\n');
  const items = [];
  for (const [hunk, h] of structuredHunks(current, proposed).entries()) {
    if (h.kind === 'block-added') {
      const reads = [...new Set(h.added.map((line) => FROM.exec(line)).filter(Boolean).map((m) => m[1]))];
      items.push({ type: 'add', text: `New ${h.type} ${h.block}${reads.length ? `, reads from ${reads.join(', ')}` : ''}`, hunk, block: h.block, isNew: true });
      continue;
    }
    if (h.kind === 'block-removed') { items.push({ type: 'remove', text: `Block ${h.block} removed`, hunk, optional: true, block: h.block }); continue; }
    const who = h.block || 'the song';
    const removed = cur.slice(h.start, h.start + h.removed).map((s) => s.trim()).filter(Boolean);
    const added = h.added.map((s) => s.trim()).filter(Boolean);
    for (let i = 0; i < Math.max(removed.length, added.length); i++) {
      const r = removed[i];
      const a = added[i];
      const rf = r ? FROM.exec(r) : null;
      const af = a ? FROM.exec(a) : null;
      if (r && a) items.push(rf && af ? { type: 'link', text: `${who} now reads from ${af[1]}.${af[2]} into ${af[3]} (before: ${rf[1]}.${rf[2]})`, hunk, block: h.block } : { type: 'change', text: changeText(who, r, a), hunk, block: h.block });
      else if (a) items.push(af ? { type: 'link', text: `${who} also reads from ${af[1]}.${af[2]} into ${af[3]}`, hunk, block: h.block } : { type: 'add', text: `${who}: ${a}`, hunk, block: h.block });
      else if (r) items.push({ type: 'remove', text: `${who}: ${r} taken out`, hunk, optional: true, block: h.block });
    }
  }
  // new blocks first, then links, changes and what goes
  const order = { add: 0, link: 1, change: 2, remove: 3 };
  items.sort((p, q) => order[p.type] - order[q.type]);
  return items.length > 8 ? [...items.slice(0, 8), { type: 'change', text: `and ${items.length - 8} more` }] : items;
}

/** The song with only some of the changes of a draft applied (hunks: the indexes of structuredHunks that stay). */
export function textWith(current, proposed, chosen) {
  const hunks = structuredHunks(current, proposed);
  return applyHunks(current, hunks.filter((_, i) => chosen.has(i)));
}

/**
 * The changes of a draft grouped per block, in the order the blocks first appear: [{ block, kind, items }]. kind is 'new' (a block that is added), 'gone' (a block that is removed) or 'changed'; changes
 * outside any block (block '') form one group of their own. Items that do not belong to a block (the closing "and N more") stay with the last group.
 */
export function groupChanges(items) {
  const groups = [];
  for (const item of items) {
    const name = item.block ?? (groups.length ? groups[groups.length - 1].block : '');
    let g = groups.find((x) => x.block === name);
    if (!g) { g = { block: name, kind: 'changed', items: [] }; groups.push(g); }
    g.items.push(item);
    if (item.isNew) g.kind = 'new';
    else if (item.type === 'remove' && /^Block .* removed$/.test(item.text)) g.kind = 'gone';
  }
  return groups;
}
