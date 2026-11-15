// The `file "..."` lines of a song and the files of the project (Remix and the Studio share this): a file that the project does not have is marked in
// the editor at once, not only after a build (the message
// of the build is AMP5350), a name that is near one file of the project gets that file as a quick fix, and after `file` the files of the project are offered. Pure, so node --test checks it.

/** The error code of a file that is not in the project, found while typing (the build says AMP5350 and, in the list of problems, AMPL920). */
export const MISSING_FILE_CODE = 'AMPL925';

const norm = (text) => [...text.toLowerCase()].filter((c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c === '.').join('');
const baseOf = (path) => path.slice(path.lastIndexOf('/') + 1);
const stemOf = (base) => { const dot = base.lastIndexOf('.'); return dot > 0 ? base.slice(0, dot) : base; };
function distance(a, b) {
  let row = Array.from({ length: b.length + 1 }, (_, j) => j);
  for (let i = 1; i <= a.length; i++) {
    const next = [i];
    for (let j = 1; j <= b.length; j++) next[j] = Math.min(row[j] + 1, next[j - 1] + 1, row[j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    row = next;
  }
  return row[b.length];
}

/**
 * The one file of the project that a name that is not there probably means, or null: the same path in other letters, the same file name in another
 * folder or with spaces and dashes left out, the same name with another
 * extension, or a name a few letters away. The first of these that finds exactly one file wins; when it finds several there is no suggestion (a guess between files is none), and a song is never one.
 */
export function similarFile(path, paths) {
  const wanted = String(path);
  const candidates = paths.filter((p) => !/[.]amplet$/i.test(p));
  const base = norm(baseOf(wanted)), stem = norm(stemOf(baseOf(wanted)));
  const steps = [
    () => candidates.filter((p) => p.toLowerCase() === wanted.toLowerCase()),
    () => (base ? candidates.filter((p) => norm(baseOf(p)) === base) : []),
    () => (stem ? candidates.filter((p) => norm(stemOf(baseOf(p))) === stem) : []),
    () => {
      if (stem.length < 4) return [];   // a short name is too little to guess from
      const limit = Math.min(3, Math.max(1, Math.floor(stem.length * 0.2)));
      const scored = candidates.map((p) => ({ p, d: distance(norm(stemOf(baseOf(p))), stem) })).filter((x) => x.d <= limit);
      const best = Math.min(...scored.map((x) => x.d));
      return scored.filter((x) => x.d === best).map((x) => x.p);
    },
  ];
  for (const step of steps) {
    const found = step();
    if (found.length === 1) return found[0];
    if (found.length > 1) return null;
  }
  return null;
}

/**
 * The marks for the `file "path"` lines of a song whose file is not among `paths` (the paths of the project): [{ line (1-based), col, endCol, severity, code, message }].
 * A value that is a pack (`pack:...`) or an address (`...://`) is not a file of the project and is left alone. A name that is near exactly one file
 * of the project says which (and has `fix`: { label, text } to put that file in its place).
 */
export function missingFileDiagnostics(text, paths) {
  const known = new Set(paths);
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
    if (!value || value.includes(':')) continue;   // a pack (pack:...), an address or another kind of reference is not a file of the project
    let path = value;
    while (path.startsWith('./')) path = path.slice(2);
    if (known.has(path)) continue;
    const quoted = `"${value}"`, at = line.indexOf(quoted);
    const near = similarFile(path, paths);
    const sameLetters = near && near.toLowerCase() === path.toLowerCase();
    out.push({
      line: i + 1, col: at + 1, endCol: at + 1 + quoted.length, severity: 'error', code: MISSING_FILE_CODE,
      message: `The file "${value}" is not in the project.${near ? ` Did you mean "${near}"?${sameLetters ? ' Names are case sensitive.' : ''}` : ' Upload it, or change the name.'}`,
      ...(near ? { fix: { label: `Use "${near}" instead`, text: `"${near}"` } } : {}),
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

/** A mark of missingFileDiagnostics as a diagnostic of the language server (0-based lines and columns), for the Studio. The fix, when there is one, is in `data`. */
export const toLspDiagnostic = (d) => ({
  range: { start: { line: d.line - 1, character: d.col - 1 }, end: { line: d.line - 1, character: d.endCol - 1 } },
  severity: 1, code: d.code, source: 'ample-files', message: d.message, ...(d.fix ? { data: { fix: d.fix } } : {}),
});
