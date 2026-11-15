// A `file "..."` line that names a file the project does not have is marked in the editor at once, not only after a build: the same message the build would give (AMP5350), found in the text.
// Pure, so node --test checks it.

/** The error code of a file that is not in the project, found while typing (the build says AMP5350 and, in the list of problems, AMPL920). */
export const MISSING_FILE_CODE = 'AMPL925';

/**
 * The marks for the `file "path"` lines of a song whose file is not among `paths` (the paths of the project): [{ line (1-based), col, endCol, severity, code, message }].
 * A value that is a pack (`pack:...`) or an address (`...://`) is not a file of the project and is left alone; a path that differs only in upper and lower case says so.
 */
export function missingFileDiagnostics(text, paths) {
  const known = new Set(paths);
  const lower = new Map(paths.map((p) => [p.toLowerCase(), p]));
  const out = [];
  const lines = String(text ?? '').split(String.fromCharCode(10));
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i];
    const t = line.trimStart();
    if (!t.startsWith('file') || !(t[4] === ' ' || t[4] === String.fromCharCode(9))) continue;
    const rest = t.slice(4).trimStart();
    if (rest[0] !== '"') continue;
    const end = rest.indexOf('"', 1);
    if (end < 0) continue;
    const value = rest.slice(1, end);
    if (!value || value.startsWith('pack:') || value.includes('://')) continue;
    let path = value;
    while (path.startsWith('./')) path = path.slice(2);
    if (known.has(path)) continue;
    const quoted = `"${value}"`, at = line.indexOf(quoted);
    const near = lower.get(path.toLowerCase());
    out.push({
      line: i + 1, col: at + 1, endCol: at + 1 + quoted.length, severity: 'error', code: MISSING_FILE_CODE,
      message: `The file "${value}" is not in the project.${near ? ` Did you mean "${near}"? Names are case sensitive.` : ' Upload it, or change the name.'}`,
    });
  }
  return out;
}
