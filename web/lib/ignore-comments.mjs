// "# ample-ignore CODE" says the person knows: the warnings and notes with that code on the line (a comment at its end) or on the next line (a comment line above, for a block header) are not shown.
// An error is never silenced. The twin of Parser.Silenced of the server, for the checks that the browser does by itself. @ample:ignore-comments

const IGNORE = /^#\s*ample-ignore\s+(.+)$/;
const codesOf = (text) => text.split(/[\s,]+/).filter(Boolean).map((c) => c.toUpperCase());

/** The codes of a trailing comment of a line (outside strings), or []. */
function trailing(line) {
  let inString = false;
  for (let k = 0; k < line.length; k++) {
    if (line[k] === '"') inString = !inString;
    else if (!inString && line[k] === '#' && !(k > 0 && /[A-Za-z0-9]/.test(line[k - 1]))) {
      const m = IGNORE.exec(line.slice(k));
      return m ? codesOf(m[1]) : [];
    }
  }
  return [];
}

/** The diagnostics (LSP shape, 0-based lines) without the ones the song silenced. */
export function withoutSilenced(diagnostics, text) {
  const lines = String(text).replace(/\r\n/g, '\n').split('\n');
  const above = new Map();
  let pending = null;
  lines.forEach((line, i) => {
    const trimmed = line.trim();
    if (trimmed.startsWith('#')) { const m = IGNORE.exec(trimmed); if (m) pending = codesOf(m[1]); return; }
    if (trimmed === '') return;
    if (pending) { above.set(i, pending); pending = null; }
  });
  return diagnostics.filter((d) => {
    if (d.severity === 1) return true;
    const line = d.range?.start?.line ?? -1;
    if (line < 0 || line >= lines.length) return true;
    const code = String(d.code || '').toUpperCase();
    return !(above.get(line)?.includes(code) || trailing(lines[line]).includes(code));
  });
}
