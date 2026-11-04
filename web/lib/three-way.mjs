// A line-based three-way merge (pure): the text that was loaded (base), the text typed in the editor (mine) and the song as it is now (theirs).
// Changes in different places merge by themselves; changes that touch the same lines are a conflict that the person has to settle.

const MAX_CELLS = 4_000_000;   // the table of the longest common subsequence; a bigger middle part is taken as one change

export const splitLines = (text) => String(text ?? '').split('\n');

/** The changes that turn a into b: [{ a0, a1, b0, b1 }] where lines a[a0..a1) become b[b0..b1). */
export function diffLines(a, b) {
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length, endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }
  const n = endA - start, m = endB - start;
  if (n === 0 && m === 0) return [];
  if (n === 0 || m === 0 || n * m > MAX_CELLS) return [{ a0: start, a1: endA, b0: start, b1: endB }];
  const width = m + 1, table = new Uint32Array((n + 1) * width);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      table[i * width + j] = a[start + i] === b[start + j] ? table[(i + 1) * width + j + 1] + 1 : Math.max(table[(i + 1) * width + j], table[i * width + j + 1]);
    }
  }
  const hunks = [];
  let i = 0, j = 0, open = null;
  const close = () => { if (open) { hunks.push(open); open = null; } };
  while (i < n || j < m) {
    if (i < n && j < m && a[start + i] === b[start + j]) { close(); i++; j++; continue; }
    open ||= { a0: start + i, a1: start + i, b0: start + j, b1: start + j };
    if (j < m && (i === n || table[i * width + j + 1] >= table[(i + 1) * width + j])) { j++; open.b1 = start + j; }
    else { i++; open.a1 = start + i; }
  }
  close();
  return hunks;
}

/** @returns {{ text: string, conflicts: number, mine: object[], theirs: object[] }} the merged text (a conflict keeps my lines) and how many places conflicted. */
export function merge3(baseText, mineText, theirsText) {
  const base = splitLines(baseText), mine = splitLines(mineText), theirs = splitLines(theirsText);
  const mineHunks = diffLines(base, mine), theirHunks = diffLines(base, theirs);
  const events = [...mineHunks.map((h) => ({ ...h, side: 'm' })), ...theirHunks.map((h) => ({ ...h, side: 't' }))].sort((x, y) => x.a0 - y.a0 || x.a1 - y.a1);
  const out = [];
  let pointer = 0, conflicts = 0;
  const apply = (hunks, lines, from, to) => {
    const part = [];
    let p = from;
    for (const h of hunks) { part.push(...base.slice(p, h.a0), ...lines.slice(h.b0, h.b1)); p = h.a1; }
    part.push(...base.slice(p, to));
    return part;
  };
  for (let k = 0; k < events.length;) {
    let from = events[k].a0, to = events[k].a1, last = k;
    while (last + 1 < events.length && events[last + 1].a0 <= to) { last++; to = Math.max(to, events[last].a1); }   // touching changes belong together
    const group = events.slice(k, last + 1), m = group.filter((h) => h.side === 'm'), t = group.filter((h) => h.side === 't');
    out.push(...base.slice(pointer, from));
    if (!t.length) out.push(...apply(m, mine, from, to));
    else if (!m.length) out.push(...apply(t, theirs, from, to));
    else {
      const mm = apply(m, mine, from, to), tt = apply(t, theirs, from, to);
      if (mm.length === tt.length && mm.every((line, index) => line === tt[index])) out.push(...mm);
      else { conflicts++; out.push(...mm); }
    }
    pointer = to; k = last + 1;
  }
  out.push(...base.slice(pointer));
  return { text: out.join('\n'), conflicts, mine: mineHunks, theirs: theirHunks };
}

/** A change as readable lines for a list: removed lines with a minus, added lines with a plus (cut after a few lines). */
export function describeHunk(base, lines, hunk, limit = 6) {
  const cut = (list, sign) => { const shown = list.slice(0, limit).map((line) => `${sign} ${line}`); if (list.length > limit) shown.push(`${sign} ... ${list.length - limit} more line${list.length - limit === 1 ? '' : 's'}`); return shown; };
  return [...cut(base.slice(hunk.a0, hunk.a1), '-'), ...cut(lines.slice(hunk.b0, hunk.b1), '+')];
}
