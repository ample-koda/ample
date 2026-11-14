// Run: node --test tools/check-long-lines.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';
import { addedLines, judge, limitsOf } from './check-long-lines.mjs';

const diffOf = (file, rows, start = 10) => [
  `diff --git a/${file} b/${file}`, `--- a/${file}`, `+++ b/${file}`, `@@ -${start},0 +${start},${rows.length} @@`,
  ...rows.map((row) => `+${row}`),
].join('\n');

const long = (n) => 'x'.repeat(n);

test('only added lines are looked at, with their file and line number', () => {
  const diff = ['+++ b/web/a.js', '@@ -3 +3,2 @@', '-  old', `+${long(150)}`, '+short', '@@ -20,0 +21 @@', `+${long(10)}`].join('\n');
  const lines = addedLines(diff);
  assert.deepEqual(lines.map((l) => [l.file, l.line, l.length]), [['web/a.js', 3, 150], ['web/a.js', 4, 5], ['web/a.js', 21, 10]]);
});

test('a line over the guideline is a warning and a line over the hard limit is an error', () => {
  const { hard, soft } = judge(diffOf('web/a.js', [long(140), long(141), long(200), long(201)]));
  assert.deepEqual(soft.map((l) => l.length), [141, 200]);
  assert.deepEqual(hard.map((l) => l.length), [201]);
});

test('the limits depend on the file type and html is wider', () => {
  assert.deepEqual(limitsOf('src/A.cs'), [140, 200]);
  assert.deepEqual(limitsOf('web/styles/app.css'), [140, 200]);
  assert.deepEqual(limitsOf('web/index.html'), [160, 240]);
  assert.equal(limitsOf('docs/notes.md'), null);
  assert.equal(limitsOf('data.json'), null);
  const { hard, soft } = judge(diffOf('web/index.html', [long(160), long(161), long(241)]));
  assert.equal(soft.length, 1);
  assert.equal(hard.length, 1);
});

test('generated and third-party files are never checked', () => {
  const files = ['web/vendor/lib.js', 'web/codemirror/x.js', 'docs/help/help.html', 'docs/api/openapi.json'];
  files.push('a/b.min.js', 'src/X/obj/y.cs', 'node_modules/a/b.js');
  for (const file of files) {
    assert.equal(limitsOf(file), null, file);
  }
});

test('a URL, a data URI, an import line and a marked line may be long', () => {
  const rows = [
    `// see https://example.com/${long(200)}`,
    `const icon = 'data:image/svg+xml;base64,${long(300)}';`,
    `import { ${long(300)} } from './x.js';`,
    `const a = ${long(300)};   // long-lines-ok: one row per line`,
    '// long-lines-ok: the rows below are a table',
    `  ['row', ${long(300)}],`,
    `const b = ${long(300)};`,
  ];
  const { hard } = judge(diffOf('web/a.js', rows));
  assert.deepEqual(hard.map((l) => l.line), [16], 'only the unmarked line');
});

test('the marker only reaches the next added line', () => {
  const { hard } = judge(diffOf('web/a.js', ['// long-lines-ok', `const a = ${long(300)};`, `const b = ${long(300)};`]));
  assert.deepEqual(hard.map((l) => l.line), [12]);
});

test('a deleted file and a hunk with only deletions add nothing', () => {
  const diff = ['--- a/web/a.js', '+++ /dev/null', '@@ -1,2 +0,0 @@', `-${long(500)}`, '-x'].join('\n');
  assert.equal(addedLines(diff).length, 0);
});

test('CRLF files do not count the carriage return', () => {
  const { soft } = judge(diffOf('src/A.cs', [`${long(140)}\r`]));
  assert.equal(soft.length, 0);
});
