// The notes of a block are the comment lines right above its header line in the song (`# ...`), so they are read in the code as well as in the Notes tab of the
// block. Pure, tested without a DOM. @ample:block-notes

const isComment = (line) => /^\s*#/.test(line) && !/^\s*# view[ :]/.test(line);

/** The note of the block whose header is on `headerLine` (0-based): the comment lines directly above it, without their `# `. Empty text when there are none. */
export function noteAbove(text, headerLine) {
  const lines = String(text || '').split('\n').map((l) => l.replace(/\r$/, ''));
  const found = [];
  for (let i = headerLine - 1; i >= 0 && isComment(lines[i]); i--) found.unshift(lines[i].replace(/^\s*#\s?/, ''));
  return found.join('\n');
}

/** The text with the comment lines above the header line replaced by this note (no comment lines when the note is empty). */
export function withNoteAbove(text, headerLine, note) {
  const lines = String(text || '').split('\n');
  let start = headerLine;
  while (start > 0 && isComment(lines[start - 1].replace(/\r$/, ''))) start--;
  const written = String(note || '').replace(/\s+$/, '').split('\n').filter((_, i, all) => all.length > 1 || all[0] !== '').map((l) => (l ? `# ${l}` : '#'));
  return [...lines.slice(0, start), ...written, ...lines.slice(headerLine)].join('\n');
}
