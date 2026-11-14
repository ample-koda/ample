#!/usr/bin/env node
// Line length of the lines a change adds. Only added lines count, so the long lines that are already in the code do not matter and
// nothing has to be reformatted. A line over the guideline is a warning; a line over the hard limit fails (exit 1).
//
//   node tools/check-long-lines.mjs                 lines added since the merge base with origin/main (committed and uncommitted)
//   node tools/check-long-lines.mjs --base main     another base
//   node tools/check-long-lines.mjs --strict        the guideline fails too
//   node tools/check-long-lines.mjs --stdin         read a unified diff (git diff -U0) from standard input
//
// An added line is left alone when it holds a URL or a data: URI, when it is an import / export line, or when `long-lines-ok` is on it
// or on the line above it (use it for a table whose rows are meant to be one line each, and say why).
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

/** [guideline, hard limit] per file type. A type that is not here has no limit. */
export const LIMITS = new Map([
  ['js', [140, 200]], ['mjs', [140, 200]], ['cjs', [140, 200]], ['ts', [140, 200]], ['cs', [140, 200]],
  ['css', [140, 200]],
  ['html', [160, 240]],
]);

/** Generated or third-party files: never checked. */
export const EXCLUDED = [
  /(^|\/)(node_modules|vendor|codemirror|bin|obj|dist|test-results|playwright-report)\//,
  /^docs\/help\//,
  /^docs\/api\//,
  /\.min\.(js|css)$/,
  /(^|\/)package-lock\.json$/,
];

export const limitsOf = (file) => {
  if (EXCLUDED.some((pattern) => pattern.test(file))) return null;
  const dot = file.lastIndexOf('.');
  return dot < 0 ? null : LIMITS.get(file.slice(dot + 1).toLowerCase()) ?? null;
};

const exempt = (text, before) => {
  if (/(https?:\/\/|\bdata:[a-z]+\/)/i.test(text)) return true;
  if (/^\s*(import|export)\b/.test(text)) return true;
  return text.includes('long-lines-ok') || before.includes('long-lines-ok');
};

/**
 * The added lines of a unified diff with no context lines: [{ file, line, length, text, before }].
 * `before` is the line above it when that is added too.
 */
export function addedLines(diff) {
  const found = [];
  let file = null, line = 0, previous = '';
  for (const row of diff.split('\n')) {
    if (row.startsWith('+++ ')) { file = row === '+++ /dev/null' ? null : row.slice(4).replace(/^b\//, ''); previous = ''; continue; }
    if (row.startsWith('--- ') || row.startsWith('diff ') || row.startsWith('index ')) continue;
    const hunk = /^@@ -\S+ \+(\d+)(?:,\d+)? @@/.exec(row);
    if (hunk) { line = Number(hunk[1]); previous = ''; continue; }
    if (row.startsWith('+') && file) {
      const text = row.slice(1).replace(/\r$/, '');
      found.push({ file, line, length: text.length, text, before: previous });
      previous = text; line++;
    } else if (!row.startsWith('-')) previous = '';
  }
  return found;
}

/** { hard, soft } lists of the added lines over the limits of their file type. */
export function judge(diff) {
  const hard = [], soft = [];
  for (const added of addedLines(diff)) {
    const limits = limitsOf(added.file);
    if (!limits || added.length <= limits[0] || exempt(added.text, added.before)) continue;
    (added.length > limits[1] ? hard : soft).push({ ...added, guideline: limits[0], limit: limits[1] });
  }
  return { hard, soft };
}

const git = (...args) => execFileSync('git', args, { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024 });

function diffSinceBase(base) {
  let mergeBase;
  try {
    mergeBase = git('merge-base', base, 'HEAD').trim();
  } catch {
    throw new Error(`no merge base with ${base}: fetch it first or pass --base`);
  }
  return git('diff', '-U0', '--no-color', '--diff-filter=AM', mergeBase);   // committed and uncommitted changes of tracked files
}

function main(argv) {
  const strict = argv.includes('--strict');
  const at = argv.indexOf('--base');
  const base = at >= 0 ? argv[at + 1] : 'origin/main';
  const diff = argv.includes('--stdin') ? readFileSync(0, 'utf8') : diffSinceBase(base);
  const { hard, soft } = judge(diff);
  const show = (kind, list) => list.forEach((l) => {
    console.log(`${kind} ${l.file}:${l.line}  ${l.length} characters (guideline ${l.guideline}, limit ${l.limit})`);
  });
  show('warning', soft);
  show('error  ', hard);
  const failed = hard.length > 0 || (strict && soft.length > 0);
  console.log(`${hard.length} over the limit, ${soft.length} over the guideline${failed ? '' : ': ok'}`);
  if (failed) {
    console.log('Split the line: one property or statement per line, a name for a repeated condition, a comment above the line.');
  }
  return failed ? 1 : 0;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try { process.exitCode = main(process.argv.slice(2)); } catch (error) { console.error(error.message); process.exitCode = 2; }
}
