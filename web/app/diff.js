// A line diff for the compare view. Pure, no DOM. @ample:compare

/**
 * Compares two texts line by line (longest common subsequence).
 * Returns [{ op: ' ' | '+' | '-', text, noNewline? }] in order.
 */
export function lineDiff(a, b) {
  const lines = (text) => {
    if (text === '') return [];
    const parts = text.split('\n'), ended = text.endsWith('\n');
    if (ended) parts.pop();
    return parts.map((value, i) => ({ text: value, ended: i < parts.length - 1 || ended }));
  };
  const x = lines(a), y = lines(b);
  const same = (left, right) => left.text === right.text && left.ended === right.ended;
  const entry = (op, line) => line.ended ? { op, text: line.text } : { op, text: line.text, noNewline: true };
  // Strip the common head and tail first: songs change in small places.
  let head = 0;
  while (head < x.length && head < y.length && same(x[head], y[head])) head++;
  let tail = 0;
  while (tail < x.length - head && tail < y.length - head && same(x[x.length - 1 - tail], y[y.length - 1 - tail])) tail++;
  const mx = x.slice(head, x.length - tail);
  const my = y.slice(head, y.length - tail);
  const out = x.slice(0, head).map((line) => entry(' ', line));
  if (mx.length * my.length > 4_000_000) {
    mx.forEach((line) => out.push(entry('-', line)));
    my.forEach((line) => out.push(entry('+', line)));
  } else {
    const n = mx.length, m = my.length;
    const L = Array.from({ length: n + 1 }, () => new Int32Array(m + 1));
    for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) L[i][j] = same(mx[i], my[j]) ? L[i + 1][j + 1] + 1 : Math.max(L[i + 1][j], L[i][j + 1]);
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (same(mx[i], my[j])) { out.push(entry(' ', mx[i])); i++; j++; } else if (L[i + 1][j] >= L[i][j + 1]) { out.push(entry('-', mx[i++])); } else { out.push(entry('+', my[j++])); }
    }
    while (i < n) out.push(entry('-', mx[i++]));
    while (j < m) out.push(entry('+', my[j++]));
  }
  x.slice(x.length - tail).forEach((line) => out.push(entry(' ', line)));
  return out;
}

/** Counts added and removed lines. */
export function diffStats(d) {
  return { added: d.filter((l) => l.op === '+').length, removed: d.filter((l) => l.op === '-').length };
}

/** Groups changed lines with surrounding context and Git-style old/new line numbers. */
export function diffHunks(d, context) {
  const oldBefore = [0], newBefore = [0];
  d.forEach((line) => { oldBefore.push(oldBefore.at(-1) + (line.op === '+' ? 0 : 1)); newBefore.push(newBefore.at(-1) + (line.op === '-' ? 0 : 1)); });
  const windows = [];
  d.forEach((line, i) => {
    if (line.op === ' ') return;
    const start = Math.max(0, i - context), end = Math.min(d.length, i + context + 1);
    if (windows.length && start <= windows.at(-1).end) windows.at(-1).end = Math.max(windows.at(-1).end, end);
    else windows.push({ start, end });
  });
  return windows.map(({ start, end }) => {
    const oldCount = oldBefore[end] - oldBefore[start], newCount = newBefore[end] - newBefore[start];
    return {
      oldStart: oldBefore[start] + (oldCount ? 1 : 0), oldCount,
      newStart: newBefore[start] + (newCount ? 1 : 0), newCount,
      lines: d.slice(start, end).map((line, i) => {
        const index = start + i;
        return { ...line, oldNumber: line.op === '+' ? null : oldBefore[index] + 1, newNumber: line.op === '-' ? null : newBefore[index] + 1 };
      }),
    };
  });
}
