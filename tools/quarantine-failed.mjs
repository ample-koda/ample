// Moves failing studio browser tests into quarantine, and back out again.
//
//   node tools/quarantine-failed.mjs <playwright-report.json> [more reports] [--dry-run]   tag the tests that failed in these reports
//   node tools/quarantine-failed.mjs --titles <file>                                         tag tests by name: a line "file.spec.ts|part of the title" each (failures read from a log)
//   node tools/quarantine-failed.mjs --list                                                  list the tests in quarantine
//   node tools/quarantine-failed.mjs --release "part of the title" [--dry-run]               take the tag off the tests whose title contains that text
//
// A test is in quarantine when its title carries " @quarantine". The normal parts of the CI and "npm run test:web" leave those tests out; the CI runs them apart,
// 10 to a job, with one worker each, and a failure there does not fail the run. The failed CI jobs keep their reports (report-*.json, in the artefact
// studio-test-failures-*); download them and give them to this script. A test that is fixed is released again with --release.
// The report comes from the JSON reporter of Playwright (the CI writes it next to the list reporter).

import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const TAG = '@quarantine';
const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..');
const specRoot = path.join(root, 'spec-tests', 'parity');

/** The first string of a test( call: its title, with the quote it uses. */
const TITLE = /(\btest(?:\.\w+)*\(\s*)(['"`])((?:\\.|(?!\2)[^\\])*)\2/;

/** Finds the failed specs of a Playwright JSON report: { file, line, title }. A spec fails when one of its tests ended unexpected. */
export function failedSpecs(report) {
  const found = [];
  const walk = (suite, file) => {
    const here = suite.file || file;
    for (const spec of suite.specs || []) {
      if (spec.ok === false) found.push({ file: spec.file || here, line: spec.line, title: spec.title });
    }
    for (const child of suite.suites || []) walk(child, here);
  };
  for (const suite of report.suites || []) walk(suite, suite.file);
  return found;
}

/** Changes the title of the test whose call starts on a line (the title may be on the next line). Returns the new text, or null when nothing was found or changed. */
export function retitle(text, line, change) {
  let start = 0;
  for (let n = 1; n < line; n++) { const next = text.indexOf(String.fromCharCode(10), start); if (next < 0) return null; start = next + 1; }
  const window = text.slice(start, start + 600);
  const match = TITLE.exec(window);
  if (!match) return null;
  const title = match[3];
  const next = change(title);
  if (next === title) return null;
  const replaced = window.replace(TITLE, (_all, open, quote) => `${open}${quote}${next}${quote}`);
  return text.slice(0, start) + replaced + text.slice(start + 600);
}

const addTag = (title) => (title.includes(TAG) ? title : `${title} ${TAG}`);
const removeTag = (title) => title.replace(new RegExp(` ?${TAG}`, 'g'), '');

/** Tags the failed tests of the reports. Returns the list of what was tagged. */
export function quarantine(reports, { dryRun = false, specDir = specRoot } = {}) {
  const tagged = [];
  const failed = reports.flatMap((report) => failedSpecs(report));
  const byFile = new Map();
  for (const spec of failed) { if (!byFile.has(spec.file)) byFile.set(spec.file, []); byFile.get(spec.file).push(spec); }
  for (const [file, specs] of byFile) {
    const full = path.isAbsolute(file) ? file : path.join(specDir, file);
    if (!fs.existsSync(full)) continue;
    let text = fs.readFileSync(full, 'utf8');
    const crlf = text.includes('\r\n');
    if (crlf) text = text.replace(/\r\n/g, '\n');
    // the later lines first, so that an edit never moves the line of an earlier one (a title only grows on its own line anyway)
    for (const spec of specs.sort((a, b) => b.line - a.line)) {
      const changed = retitle(text, spec.line, addTag);
      if (changed !== null) { text = changed; tagged.push(`${path.basename(full)}:${spec.line} ${spec.title}`); }
    }
    if (!dryRun) fs.writeFileSync(full, crlf ? text.replace(/\n/g, '\r\n') : text);
  }
  return tagged;
}

/**
 * Tags tests by name instead of by report: each entry is "file.spec.ts|part of the title" (for failures that were read from a log).
 * A part that matches no test, or more than one in its file, is reported in "problems" and changes nothing.
 */
export function quarantineByTitle(entries, { dryRun = false, specDir = specRoot } = {}) {
  const tagged = [];
  const problems = [];
  for (const entry of entries) {
    const [file, part] = entry.split('|');
    const full = path.join(specDir, file);
    if (!fs.existsSync(full) || !part) { problems.push(`${entry}: no such spec file or no title part`); continue; }
    let text = fs.readFileSync(full, 'utf8');
    const crlf = text.includes('\r\n');
    if (crlf) text = text.split('\r\n').join('\n');
    const matches = [...text.matchAll(new RegExp(TITLE.source, 'g'))].filter((m) => m[3].includes(part));
    if (matches.length !== 1) { problems.push(`${entry}: ${matches.length} tests match`); continue; }
    const line = text.slice(0, matches[0].index).split('\n').length;
    const changed = retitle(text, line, addTag);
    if (changed === null) continue;   // already in quarantine
    tagged.push(`${file}:${line} ${matches[0][3]}`);
    if (!dryRun) fs.writeFileSync(full, crlf ? changed.split('\n').join('\r\n') : changed);
  }
  return { tagged, problems };
}

/** Lists the quarantined tests (file, line of the test( call, title) of a folder of specs. */
export function listQuarantined(specDir = specRoot) {
  const out = [];
  for (const name of fs.readdirSync(specDir).filter((n) => n.endsWith('.spec.ts')).sort()) {
    const text = fs.readFileSync(path.join(specDir, name), 'utf8').split('\r\n').join('\n');
    for (const match of text.matchAll(new RegExp(TITLE.source, 'g'))) {
      if (!match[3].includes(TAG)) continue;
      out.push({ file: name, line: text.slice(0, match.index).split(String.fromCharCode(10)).length, title: match[3] });
    }
  }
  return out;
}

/** Takes the tag off the tests whose title contains the given text. Returns what was released. */
export function release(text, { dryRun = false, specDir = specRoot } = {}) {
  const released = [];
  for (const item of listQuarantined(specDir).filter((q) => q.title.includes(text))) {
    const full = path.join(specDir, item.file);
    let content = fs.readFileSync(full, 'utf8');
    const crlf = content.includes('\r\n');
    if (crlf) content = content.replace(/\r\n/g, '\n');
    const changed = retitle(content, item.line, removeTag);
    if (changed === null) continue;
    released.push(`${item.file}:${item.line} ${removeTag(item.title)}`);
    if (!dryRun) fs.writeFileSync(full, crlf ? changed.replace(/\n/g, '\r\n') : changed);
  }
  return released;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const dryRun = args.includes('--dry-run');
  const rest = args.filter((a) => a !== '--dry-run');
  if (rest[0] === '--list') {
    const list = listQuarantined();
    for (const q of list) console.log(`${q.file}:${q.line}  ${q.title}`);
    console.log(`${list.length} test(s) in quarantine`);
  } else if (rest[0] === '--release') {
    if (!rest[1]) { console.error('Give part of the title of the test to release.'); process.exit(2); }
    const out = release(rest[1], { dryRun });
    for (const line of out) console.log(`${dryRun ? 'would release' : 'released'}  ${line}`);
    if (!out.length) console.log('No quarantined test has that text in its title.');
  } else if (rest[0] === '--titles') {
    if (!rest[1]) { console.error('Give a file with one "file.spec.ts|part of the title" on each line.'); process.exit(2); }
    const entries = fs.readFileSync(rest[1], 'utf8').split(/\r?\n/).map((l) => l.trim()).filter(Boolean);
    const { tagged, problems } = quarantineByTitle(entries, { dryRun });
    for (const line of tagged) console.log(`${dryRun ? 'would quarantine' : 'quarantined'}  ${line}`);
    for (const line of problems) console.error(`problem  ${line}`);
    console.log(`${tagged.length} test(s) ${dryRun ? 'would be ' : ''}put in quarantine, ${problems.length} problem(s).`);
    if (problems.length) process.exitCode = 1;
  } else if (rest.length) {
    const reports = rest.map((p) => JSON.parse(fs.readFileSync(p, 'utf8')));
    const tagged = quarantine(reports, { dryRun });
    for (const line of tagged) console.log(`${dryRun ? 'would quarantine' : 'quarantined'}  ${line}`);
    console.log(tagged.length ? `${tagged.length} test(s) ${dryRun ? 'would be ' : ''}put in quarantine.` : 'No new failed test to put in quarantine.');
  } else {
    console.error('Usage: node tools/quarantine-failed.mjs <report.json>... [--dry-run] | --list | --release "title text" [--dry-run]');
    process.exit(2);
  }
}
