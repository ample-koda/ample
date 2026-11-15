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

/**
 * Whether the caret is on the value of a `file` line, and where that value starts. `before` is the text of the line up to the caret and `after` the text after it.
 * Returns { from (column, 0-based, where the typed value starts), prefix (what is typed of it), open (it starts with a quote), closed (the closing quote is there already) } or null.
 */
export function fileValueContext(before, after = '') {
  const t = before.trimStart(), indent = before.length - t.length;
  if (!t.startsWith('file') || !(t[4] === ' ' || t[4] === String.fromCharCode(9))) return null;
  const rest = t.slice(4), value = rest.trimStart(), from = indent + 4 + (rest.length - value.length);
  if (value.startsWith('"')) {
    const inner = value.slice(1);
    return inner.includes('"') ? null : { from: from + 1, prefix: inner, open: true, closed: after.startsWith('"') };
  }
  if (value.includes('"') || value.includes('#')) return null;
  return { from, prefix: value, open: false, closed: false };
}

const SOUND = ['wav', 'flac', 'mp3', 'ogg', 'aif', 'aiff', 'sf2'];
const kindOf = (path) => { const ext = path.slice(path.lastIndexOf('.') + 1).toLowerCase(); return SOUND.includes(ext) ? 0 : 1; };

/**
 * The files of the project to offer for a `file` value: the sound files first, then the others, never a song; each as { label, detail }. `paths` are the paths of the project.
 * What is typed is matched anywhere in the path, so a folder or a part of a name finds it.
 */
export function fileChoices(paths, prefix = '') {
  const lower = String(prefix).toLowerCase();
  return paths
    .filter((p) => !/[.]amplet$/i.test(p) && p.toLowerCase().includes(lower))
    .map((p) => ({ label: p, detail: kindOf(p) === 0 ? 'sound file' : 'file', rank: kindOf(p) }))
    .sort((a, b) => a.rank - b.rank || a.label.localeCompare(b.label))
    .map(({ label, detail }) => ({ label, detail }));
}
