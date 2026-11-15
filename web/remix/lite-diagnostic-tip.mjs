// The text of the tip over a marked place in the editor: what the problem is (the message of the diagnostic), then the help of the language server for the word, when there is some. Pure, so node --test checks it.

const WORD = { error: 'Error', warning: 'Warning', info: 'Note' };

/** The diagnostics ({ line (1-based), col (1-based), endCol (end, 1-based, not included), severity, code, message }) that cover a place of the text ({ line (0-based), character }); a mark is at least one character wide, as it is drawn. */
export function diagnosticsAt(list, at) {
  return (list ?? []).filter((d) => {
    if ((d.line || 1) - 1 !== at.line) return false;
    const from = Math.max(0, (d.col || 1) - 1);
    const to = Math.max(from + 1, (d.endCol || d.col || 1) - 1);
    return at.character >= from && at.character < to;
  });
}

/** The text of the tip: a line for each problem ("Error AMPL925: ..."), then a blank line and the help of the language server, when there is any. */
export function tipText(hits, markdown) {
  const NL = String.fromCharCode(10);
  const parts = hits.map((d) => `${WORD[d.severity] ?? 'Note'}${d.code ? ` ${d.code}` : ''}: ${d.message}`);
  const help = String(markdown ?? '').replace(/[`*_]/g, '').trim();
  if (help) parts.push(help);
  return parts.join(NL + NL);
}

/** The class of the tip: red for an error, yellow for a warning, none when there is only help. */
export const tipClass = (hits) => `lite-tip${hits.some((d) => d.severity === 'error') ? ' lite-tip-error' : hits.length ? ' lite-tip-warning' : ''}`;
