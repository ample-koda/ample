// A short line diff for what Koda is told about unsaved changes (plan/editor-tabs.md, part 5): the saved text against the working text, as a few hunks of changed lines with a little context.

/** Lines that were removed ("-") and added ("+") between two texts, grouped in hunks; at most `limit` lines are shown, then a count of what was left out. */
export function lineDiff(saved, working, limit = 60) {
  const a = String(saved ?? '').split('\n'), b = String(working ?? '').split('\n');
  if (a.join('\n') === b.join('\n')) return '(no difference)';
  // the common beginning and end are skipped; the rest is compared with a longest common subsequence while it is small
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
  const mid = { a: a.slice(head, a.length - tail), b: b.slice(head, b.length - tail) };
  const out = [];
  const edits = mid.a.length * mid.b.length <= 4_000_000 ? lcsEdits(mid.a, mid.b) : [...mid.a.map((line) => ['-', line]), ...mid.b.map((line) => ['+', line])];
  const before = a.slice(Math.max(0, head - 2), head);
  out.push(`@@ around line ${head + 1} @@`, ...before.map((line) => ` ${line}`));
  for (const [kind, line] of edits) out.push(`${kind}${line}`);
  out.push(...a.slice(a.length - tail, a.length - tail + 2).map((line) => ` ${line}`));
  if (out.length <= limit) return out.join('\n');
  return `${out.slice(0, limit).join('\n')}\n... (${out.length - limit} more lines of the difference left out)`;
}

function lcsEdits(a, b) {
  const rows = a.length, cols = b.length;
  const table = Array.from({ length: rows + 1 }, () => new Uint32Array(cols + 1));
  for (let i = rows - 1; i >= 0; i--) for (let j = cols - 1; j >= 0; j--) table[i][j] = a[i] === b[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
  const edits = [];
  let i = 0, j = 0;
  while (i < rows && j < cols) {
    if (a[i] === b[j]) { i++; j++; } else if (table[i + 1][j] >= table[i][j + 1]) edits.push(['-', a[i++]]); else edits.push(['+', b[j++]]);
  }
  while (i < rows) edits.push(['-', a[i++]]);
  while (j < cols) edits.push(['+', b[j++]]);
  return edits;
}
