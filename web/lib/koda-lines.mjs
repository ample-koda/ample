// The change a draft of Koda makes to the text of the song, as hunks of lines: where in the current text, how many lines go, which lines come. Pure. @ample:koda

/** Hunks between two texts: [{ start, removed, added }] where start is the 0-based line of the current text where the change begins, removed how many lines of it go and added the lines that come in their place. */
export function lineHunks(current, proposed) {
  const a = String(current ?? '').split('\n');
  const b = String(proposed ?? '').split('\n');
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
  const x = a.slice(head, a.length - tail);
  const y = b.slice(head, b.length - tail);
  if (!x.length && !y.length) return [];
  if (x.length * y.length > 4_000_000) return [{ start: head, removed: x.length, added: y }];   // a very large change: one hunk
  // longest common subsequence of the middle, then the runs between the common lines are the hunks
  const n = x.length, m = y.length;
  const table = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) table[i][j] = x[i] === y[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const hunks = [];
  let i = 0, j = 0, open = null;
  const flush = () => { if (open) { hunks.push(open); open = null; } };
  while (i < n || j < m) {
    if (i < n && j < m && x[i] === y[j]) { flush(); i++; j++; continue; }
    if (!open) open = { start: head + i, removed: 0, added: [] };
    if (j < m && (i >= n || table[i][j + 1] >= table[i + 1][j])) { open.added.push(y[j]); j++; } else { open.removed++; i++; }
  }
  flush();
  return hunks;
}

/** The text with the hunks applied (to check a diff: applying it to the current text gives the proposed text). */
export function applyHunks(current, hunks) {
  const lines = String(current ?? '').split('\n');
  for (const h of [...hunks].sort((p, q) => q.start - p.start)) lines.splice(h.start, h.removed, ...h.added);
  return lines.join('\n');
}
