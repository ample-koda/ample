// Where a failed build points in the code (pure, tested without a DOM): which line of the song a failed block is blamed on, and which of those marks are
// still valid for the text now in the editor. A mark goes away as soon as its line is edited and comes back with the next build. @ample:build-errors

const unquote = (v) => String(v ?? '').trim().replace(/^"|"$/g, '');

/**
 * The line (0-based) a failure is blamed on: the parameter whose value or name the message mentions, otherwise the header line of the block.
 * @param {{ name: string, line: number, parameters?: { name: string, value: string, line?: number }[] }} block
 */
export function errorLineFor(block, message) {
  const text = String(message ?? '');
  const params = (block.parameters || []).filter((p) => Number.isInteger(p.line));
  const byValue = params.find((p) => unquote(p.value).length >= 3 && text.includes(unquote(p.value)));
  if (byValue) return byValue.line;
  const byName = params.find((p) => new RegExp(`['"\`]${p.name}['"\`]|\\b${p.name}\\b`).test(text));
  return byName ? byName.line : block.line;
}

/** The marks of a finished build: for every failed block the message, its code and the line with the text it had when the build ran. */
export function collectBuildErrors(report, doc, textLines) {
  const items = {};
  for (const b of report?.blocks || []) {
    if ((b.status ?? b.Status) !== 'failed') continue;
    const name = b.block ?? b.Block, message = b.message ?? b.Message ?? '';
    const block = (doc?.blocks || []).find((x) => x.name === name || x.Name === name);
    if (!block) continue;
    const line = errorLineFor({ name, line: block.line ?? block.Line, parameters: (block.parameters || []).map((p) => ({ name: p.name ?? p.Name, value: p.value ?? p.Value, line: p.line ?? p.Line })) }, message);
    if (!Number.isInteger(line) || line < 0 || line >= textLines.length) continue;
    items[name] = { message, code: /^(AMP\d+)/.exec(message)?.[1] || '', line, text: textLines[line] };
  }
  return items;
}

/** The marks that are still true for these lines of text: a line that was edited has no mark. Returns { lineIndex: item }. */
export function liveMarks(items, textLines) {
  const marks = {};
  for (const [name, item] of Object.entries(items || {})) {
    // lines above may have moved it: the nearest line that still has the same text (a line such as `fingerprint content` occurs in every block)
    let at = -1;
    for (let d = 0; d < textLines.length && at < 0; d++) {
      if (textLines[item.line - d] === item.text) at = item.line - d;
      else if (textLines[item.line + d] === item.text) at = item.line + d;
    }
    if (at < 0 || !item.text.trim()) continue;
    if (marks[at]) { marks[at].messages.push(item.message); continue; }
    marks[at] = { blocks: [name], messages: [item.message], code: item.code };
    if (marks[at].blocks.length === 0) delete marks[at];
  }
  return marks;
}

/**
 * The failed blocks of the last build as text for Koda: a build error is not a parse problem, so the song check of the assistant may show none, and "fix the compile errors" would find
 * nothing. Empty when nothing failed. At most 12 blocks; each with its message and the line it is blamed on.
 */
export function buildErrorsText(items) {
  const rows = Object.entries(items || {}).slice(0, 12).map(([name, item]) => `- block ${name}: ${String(item.message ?? '').replace(/\s+/g, ' ').trim()}${item.text?.trim() ? ` (blamed on the line: ${String(item.text).trim().slice(0, 160)})` : ''}`);
  return rows.length ? `\n\nThe last build failed in these blocks (a build error is not a parse problem, so the song check may show none: read the message, find its cause (often a line the user changed, see the changes below) and fix that; never answer that there are no compile errors while this list is here):\n${rows.join('\n')}` : '';
}
