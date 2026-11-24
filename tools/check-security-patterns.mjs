#!/usr/bin/env node
// Fails when the C# sources use a pattern that caused a security finding (plan/security-findings.md has the story of each rule).
// A line that is meant to use such a pattern says why in a comment on the same line or the line above: `// security-ok: <reason>`.
// Usage: node tools/check-security-patterns.mjs [--list]
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';

const root = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..');

/** Each rule: what to look for, in which files, and what to do instead. */
export const RULES = [
  {
    id: 'path-without-link-check',
    // the form that never looks at the disk: a folder of the project can be a link (junction, symlink) to somewhere else
    pattern: /ProjectPath\.Resolve\(\s*[\w.]*(?:Root|ProjectRoot)\b\s*,/,
    only: /(^|\/)src\/Ample\.(Server|Storage)\//,
    use: 'ProjectPath.Resolve(fs, root, path): it also refuses a link that leads out of the project',
  },
  {
    id: 'private-folder-by-text',
    // "./.ample/x", "a/../.ample/x" and ".AMPLE/x" start with something else than ".ample"
    pattern: /StartsWith\(\s*(?:"\.ample(?=["\/\\])|(?:ProjectLayout\.)?AmpleFolder\b)/,
    only: /(^|\/)src\//,
    use: 'ProjectPath.IsPrivate(path): it resolves the path first',
  },
  {
    id: 'zip-without-limits',
    // a small zip can expand to gigabytes or name an entry that climbs out of the folder
    pattern: /ExtractToDirectory\(|\.ExtractToFile\(|ZipFileExtensions\.ExtractToFile/,
    only: /(^|\/)src\//,
    skip: /(^|\/)SafeZip\.cs$/,
    use: 'SafeZip (counts the bytes that are really produced, refuses paths that leave the folder)',
  },
  {
    id: 'cmd-exe',
    // cmd reads & | ^ < > as commands in an argument that is not inside quotes, and .NET only quotes an argument that holds a space
    pattern: /"cmd(?:\.exe)?"/,
    only: /(^|\/)(src|editors)\//,
    skip: /(^|\/)Junction\.cs$/,
    use: 'no shell: the program itself with ArgumentList (for a junction: Junction.TryCreate)',
  },
  {
    id: 'argument-string-with-path',
    // an Arguments string made by joining text: a space, a quote or a leading "-" in the text becomes another argument or option
    pattern: /new\s*\(\s*[^()]*,\s*\$?"[^"]*\{[^}]*\}[^"]*"\s*\)\s*\{[^}]*UseShellExecute\s*=\s*false/,
    only: /(^|\/)src\/Ample\.(Server|Plugins|Audio|Storage)\//,
    use: 'ArgumentList.Add(...) for every argument',
  },
  {
    id: 'shell-execute',
    // ShellExecute opens anything (a program, a document, a protocol): the text must be checked first
    pattern: /UseShellExecute\s*=\s*true/,
    only: /(^|\/)(src|editors)\//,
    use: 'check the text first (LaunchPolicy.IsStudioAddress / IsFolder) or start the program itself with UseShellExecute = false',
  },
  {
    id: 'path-from-text-combined',
    // Path.Combine(root, text-from-outside) keeps an absolute second part and "..": the result can be anywhere
    pattern: /Path\.Combine\(\s*targetRoot\s*,/,
    only: /(^|\/)src\//,
    use: 'ProjectPath.Resolve(fs, root, path) (see TxImport.SafeDestination)',
  },
  {
    id: 'plugin-from-project',
    // a plugin manifest names the program to run: only the installed folders count, never a folder inside a project
    pattern: /PluginHost\.Discover\([^;]*(?<![A-Za-z])(?:s\.Root|Root)\s*,\s*"plugins"/,
    only: /(^|\/)src\//,
    use: 'InstalledPluginFolders(...)',
  },
  {
    id: 'xml-with-entities',
    // XDocument.Parse and XmlDocument expand the entities of a DOCTYPE: a few hundred bytes become gigabytes (a "billion laughs" file)
    pattern: /XDocument\.Parse\(|XDocument\.Load\((?!xmlReader\b)|new\s+XmlDocument\b|XmlDocument\(\)/,
    only: /(^|\/)(src|editors)\//,
    use: 'XmlReader.Create(reader, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }) and XDocument.Load(xmlReader)',
  },
  {
    id: 'parse-of-song-number-throws',
    // int.Parse and long.Parse throw an OverflowException on twenty digits, and the parsers catch only FormatException: one word in a song ended the parse
    pattern: /\b(?:int|long|uint|short|byte)\.Parse\(\s*(?:\w+\.)?(?:\w+\.)?Groups\[|\blong\.Parse\(\s*text\.AsSpan/,
    only: /(^|\/)src\/Ample\.(Language|Engine|Audio|Server|Storage)\//,
    use: 'int.TryParse(..., out value) and a diagnostic or a bad note when it fails; or a regex that limits the digits ({1,9}) with a security-ok reason',
  },
  {
    id: 'ffmpeg-input-without-protocol-whitelist',
    // ffmpeg opens playlists and scripts named inside a file it was given (http, tcp, concat): an input that is not a pipe is limited to local files
    pattern: /^(?!.*protocol_whitelist).*"-i",\s+(?!"pipe:0")/,
    only: /(^|\/)src\//,
    use: '"-protocol_whitelist", "file" before "-i"',
  },
];

function files() {
  const out = execFileSync('git', ['ls-files', '*.cs'], { cwd: root, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return out.split('\n').filter((f) => f && !/(^|\/)(bin|obj)\//.test(f) && !/(^|\/)tests\//.test(f));
}

export function check(listAll = false) {
  const hits = [];
  for (const file of files()) {
    const lines = fs.readFileSync(path.join(root, file), 'utf8').split('\n');
    lines.forEach((line, i) => {
      for (const rule of RULES) {
        if (!rule.only.test(file) || (rule.skip && rule.skip.test(file)) || !rule.pattern.test(line)) continue;
        const marked = /security-ok:/.test(line) || (i > 0 && /security-ok:/.test(lines[i - 1]));
        if (marked && !listAll) continue;
        hits.push({ file, line: i + 1, rule: rule.id, use: rule.use, marked });
      }
    });
  }
  return hits;
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1'))) {
  const list = process.argv.includes('--list');
  const hits = check(list);
  for (const h of hits) console.log(`${h.marked ? 'ok   ' : 'error'} ${h.file}:${h.line}  [${h.rule}]  use ${h.use}`);
  const bad = hits.filter((h) => !h.marked);
  const failed = `check-security-patterns: ${bad.length} place(s) use a pattern that caused a finding (plan/security-findings.md);`
    + ' mark a deliberate one with "// security-ok: <reason>"';
  console.log(bad.length === 0 ? 'check-security-patterns: ok' : failed);
  process.exit(bad.length === 0 ? 0 : 1);
}
