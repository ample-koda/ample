// What the last edit changed in the song text, for the dock of the Playlist ("the Edits list"): the lines that went and the lines that came, with the lines before and after
// that stayed out of it. A plain comparison of the common start and end of two texts. Pure, tested without a DOM. @ample:playlist-diff

/** { removed: [...], added: [...], line } for two texts (line is the first changed line, counted from 1), or null when they are equal. */
export function lastChange(before, after, max = 12) {
  if (before === after) return null;
  const a = String(before ?? '').split('\n'), b = String(after ?? '').split('\n');
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) start++;
  let endA = a.length, endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB--; }
  const cut = (lines) => (lines.length > max ? [...lines.slice(0, max), `... ${lines.length - max} more lines`] : lines);
  return { line: start + 1, removed: cut(a.slice(start, endA)), added: cut(b.slice(start, endB)) };
}
