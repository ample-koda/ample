// A sequence can be kept in the song itself: a `sequence { ... }` part of the sequencer block that holds the text of a sequence file. These are the text edits that
// read it, change it, move a file into the song (embed) and move it out to a file (extract). Pure, tested without a DOM. @ample:embedded-seq

const indentOf = (line) => line.length - line.trimStart().length;

/** Where the sequence { } part of the block that starts on `headerLine` (0-based) is, and where the block ends: { open, close, blockEnd, indent } (lines, 0-based; open and close are -1 when there is none). */
export function sequencePart(text, headerLine) {
  const lines = String(text).split('\n').map((l) => l.replace(/\r$/, ''));
  const blockIndent = indentOf(lines[headerLine] ?? '');
  let blockEnd = lines.length - 1, open = -1, close = -1;
  for (let i = headerLine + 1; i < lines.length; i++) {
    const trimmed = lines[i].trim();
    if (trimmed === '}' && indentOf(lines[i]) === blockIndent) { blockEnd = i; break; }
    if (open < 0 && trimmed === 'sequence {') {
      open = i;
      const own = indentOf(lines[i]);
      for (let k = i + 1; k < lines.length; k++) if (lines[k].trim() === '}' && indentOf(lines[k]) === own) { close = k; break; }
      if (close < 0) close = lines.length - 1;
      i = close;
    }
  }
  return { open, close, blockEnd, indent: blockIndent + 2, lines };
}

/** The text of the sequence kept in the block, without the indent it has in the song; null when the block keeps none. */
export function readEmbedded(text, headerLine) {
  const part = sequencePart(text, headerLine);
  if (part.open < 0) return null;
  const strip = indentOf(part.lines[part.open]) + 2;   // the body is taken as the song text has it, less the indent of the part
  return part.lines.slice(part.open + 1, part.close).map((line) => line.slice(Math.min(strip, indentOf(line)))).join('\n');
}

const asPart = (seqText, indent) => {
  const pad = ' '.repeat(indent);
  return [`${pad}sequence {`, ...String(seqText).replace(/\s+$/, '').split('\n').map((line) => (line ? `${' '.repeat(indent + 2)}${line}` : '')), `${pad}}`];
};

/** The song text with the sequence kept in the block replaced by this one (added at the end of the block when there was none). */
export function withEmbedded(text, headerLine, seqText) {
  const part = sequencePart(text, headerLine);
  const block = asPart(seqText, part.indent);
  const lines = part.lines;
  if (part.open >= 0) return [...lines.slice(0, part.open), ...block, ...lines.slice(part.close + 1)].join('\n');
  return [...lines.slice(0, part.blockEnd), ...block, ...lines.slice(part.blockEnd)].join('\n');
}

/** Moves a sequence file into the song: the file parameter of the block goes and its text is kept in a sequence { } part in its place. The file itself is not touched. */
export function embedFile(text, headerLine, seqText) {
  const part = sequencePart(text, headerLine);
  const fileLine = part.lines.findIndex((line, i) => i > headerLine && i < part.blockEnd && /^\s*file\s/.test(line));
  const block = asPart(seqText, part.indent);
  const lines = part.lines;
  if (part.open >= 0) return withEmbedded(text, headerLine, seqText);
  if (fileLine < 0) return [...lines.slice(0, part.blockEnd), ...block, ...lines.slice(part.blockEnd)].join('\n');
  return [...lines.slice(0, fileLine), ...block, ...lines.slice(fileLine + 1)].join('\n');
}

/** Takes the sequence out of the song: its part is replaced by a file parameter that names the file (the text itself is written to that file by the caller). */
export function extractToFile(text, headerLine, path) {
  const part = sequencePart(text, headerLine);
  if (part.open < 0) return text;
  const value = /^[A-Za-z0-9_./:@+-]+$/.test(path) ? path : `"${path.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
  const lines = part.lines;
  return [...lines.slice(0, part.open), `${' '.repeat(part.indent)}file ${value}`, ...lines.slice(part.close + 1)].join('\n');
}
