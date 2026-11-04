// Which words of a song line are links while Ctrl is held: the name of a block (in its header and in a from line) and the path of a project file
// (the value of a file parameter, or any path that is one of the project's files). Pure, no DOM. @ample:source-links

/**
 * The links of one line as { start, end, kind, target } (end exclusive, in characters).
 * @param {string} line the text of the line
 * @param {string[]} blockNames the names of the blocks of the song
 * @param {string[]} filePaths the project files (relative paths)
 */
import { HEADER_TAIL } from './block-header.mjs';

const BLOCK_HEAD = new RegExp(String.raw`^(\s*[\w-]+\s+)("(?:[^"\\]|\\.)*"|[\w.-]+)` + HEADER_TAIL);

export function linksIn(line, blockNames, filePaths) {
  const out = [];
  const names = new Set(blockNames);
  const files = new Set(filePaths);
  const push = (start, text, kind, target, extra = {}) => out.push({ start, end: start + text.length, kind, target, ...extra });
  // block header: <type> <name> [pinned@<version>] ... {
  const head = BLOCK_HEAD.exec(line);
  if (head) {
    const raw = head[2];
    const name = raw.startsWith('"') ? raw.slice(1, -1) : raw;
    if (names.has(name)) push(head[1].length, raw, 'block', name);
  }
  // from <block>.<output> -> <input>
  const from = /^(\s*from\s+)("(?:[^"\\]|\\.)*"|[\w-]+)(?=\.)/.exec(line);
  if (from) {
    const raw = from[2];
    const name = raw.startsWith('"') ? raw.slice(1, -1) : raw;
    if (names.has(name)) push(from[1].length, raw, 'block', name);
    const connection = /^\s*from\s+("(?:[^"\\]|\\.)*"|[\w-]+)\.("(?:[^"\\]|\\.)*"|[\w-]+)\s*(->)\s*("(?:[^"\\]|\\.)*"|[\w-]+)/.exec(line);
    if (connection && names.has(name)) {
      const unquote = (value) => value.startsWith('"') ? value.slice(1, -1).replace(/\\(.)/g, '$1') : value;
      const arrowAt = line.indexOf('->', connection.index);
      push(arrowAt, '->', 'connection', name, { source: unquote(connection[1]), output: unquote(connection[2]), input: unquote(connection[4]) });
    }
  }
  // a path of the project: quoted or not, with a folder or an extension
  const word = /"((?:[^"\\]|\\.)+)"|([\w./-]+\.[A-Za-z0-9]+)/g;
  for (let m = word.exec(line); m; m = word.exec(line)) {
    const text = m[1] ?? m[2];
    const start = m.index + (m[1] !== undefined ? 1 : 0);
    if (files.has(text) && !out.some((l) => l.start <= start && l.end > start)) push(start, text, 'file', text);
  }
  return out.sort((a, b) => a.start - b.start);
}

/** The link at a character position of a line, or null. */
export const linkAt = (line, ch, blockNames, filePaths) => linksIn(line, blockNames, filePaths).find((l) => ch >= l.start && ch < l.end) || null;
