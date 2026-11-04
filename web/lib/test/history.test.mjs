import test from 'node:test';
import assert from 'node:assert/strict';
import { lineDiff, diffStats, diffHunks } from '../../app/diff.js';
import { compareRefs, histModel, lineChain, lineChildren, lineParent, shortId, visibleNodes } from '../../app/history-model.js';

const at = '2026-10-03T10:00:00+00:00';
const graph = (extra = {}) => ({
  current: 'garage',
  lines: [
    { name: 'garage', kind: 'garage', head: 'n4', archived: false },
    { name: 'studio', kind: 'studio', head: 'n2', archived: false },
    { name: 'try', kind: 'work', head: 'n2', archived: false },
    { name: 'old', kind: 'work', head: 'n5', archived: true },
  ],
  tags: [{ name: 'v1', nodeId: 'n2', reason: '' }],
  nodes: [
    { nodeId: 'n1', parentId: null, kind: 'start', message: 'first', at, files: 1 },
    { nodeId: 'n2', parentId: 'n1', kind: 'checkpoint', message: 'two', at, files: 1 },
    { nodeId: 'n3', parentId: 'n2', kind: 'checkpoint', message: 'three', at, files: 1 },
    { nodeId: 'n4', parentId: 'n3', kind: 'checkpoint', message: 'four', at, files: 1 },
    { nodeId: 'n5', parentId: 'n2', kind: 'checkpoint', message: 'five', at, files: 1 },
  ],
  ...extra,
});

test('a line diff finds added, removed and unchanged lines', () => {
  const d = lineDiff('a\nb\nc', 'a\nx\nc\nd');
  assert.deepEqual(d.map((l) => l.op + l.text), [' a', '-b', '-c', '+x', '+c', '+d']);
  assert.deepEqual(diffStats(d), { added: 3, removed: 2 });
});

test('identical texts and empty texts', () => {
  assert.deepEqual(diffStats(lineDiff('same\ntext', 'same\ntext')), { added: 0, removed: 0 });
  assert.deepEqual(diffStats(lineDiff('', 'a\nb')), { added: 2, removed: 0 });
  assert.deepEqual(diffStats(lineDiff('a\nb', '')), { added: 0, removed: 2 });
});

test('a final newline does not count as an empty line but its absence remains visible', () => {
  const added = lineDiff('', 'one line\n');
  assert.deepEqual(diffStats(added), { added: 1, removed: 0 });
  assert.deepEqual(added.map((line) => line.text), ['one line']);
  const changedEnding = lineDiff('one line\n', 'one line');
  assert.deepEqual(diffStats(changedEnding), { added: 1, removed: 1 });
  assert.equal(changedEnding.find((line) => line.op === '+').noNewline, true);
});

test('diff hunks include context and old/new line numbers', () => {
  const hunks = diffHunks(lineDiff('head\nold\ntail', 'head\nnew\ntail'), 1);
  assert.equal(hunks.length, 1);
  assert.deepEqual([hunks[0].oldStart, hunks[0].oldCount, hunks[0].newStart, hunks[0].newCount], [1, 3, 1, 3]);
  assert.deepEqual(hunks[0].lines.map((line) => [line.op, line.oldNumber, line.newNumber]), [[' ', 1, 1], ['-', 2, null], ['+', null, 2], [' ', 3, 3]]);
});

test('lines own the revisions up to where another line owns them', () => {
  const m = histModel(graph());
  assert.equal(m.lineOf.get('n4'), 'garage');
  assert.equal(m.lineOf.get('n3'), 'garage');
  assert.equal(m.lineOf.get('n2'), 'studio');
  assert.equal(m.hereId, 'n4');
  assert.equal(m.studioId, 'n2');
  assert.deepEqual(m.tagsAt.get('n2'), ['v1']);
});

test('a line forked from a revision of another line has that line as its parent, also while it has no revision of its own', () => {
  const m = histModel(graph());
  assert.equal(lineParent(m, 'garage'), 'studio');
  assert.equal(lineParent(m, 'try'), 'studio');
  assert.equal(lineParent(m, 'studio'), null);
  assert.deepEqual(lineChain(m, 'garage'), ['studio', 'garage']);
  assert.deepEqual(lineChildren(m, 'studio', false).sort(), ['garage', 'try']);
  assert.ok(lineChildren(m, 'studio', true).includes('old'));
});

test('the simple view hides archived lines and the advanced view shows everything', () => {
  const m = histModel(graph());
  const simple = visibleNodes(m, false).map((n) => n.nodeId);
  assert.ok(!simple.includes('n5') || m.lineOf.get('n5') !== 'old');
  assert.equal(visibleNodes(m, true).length, 5);
});

test('ids are shown short without the prefix', () => {
  assert.equal(shortId('node:0123456789abcdef'), '01234567');
  assert.equal(shortId(null), '-');
});

test('compare retains an older selected revision outside the recent twenty', () => {
  const nodes = Array.from({ length: 25 }, (_, i) => ({ nodeId: `n${i + 1}`, parentId: i ? `n${i}` : null, kind: 'checkpoint', message: `revision ${i + 1}`, at, files: 1 }));
  const m = histModel(graph({ nodes, lines: [{ name: 'studio', head: 'n1', archived: false }, { name: 'garage', head: 'n25', archived: false }] }));
  const refs = compareRefs(m, 'n2', 'n25');
  assert.equal(refs.filter(([id]) => id === 'n2').length, 1);
  assert.ok(refs.find(([id]) => id === 'n2')[1].includes('revision 2'));
  assert.equal(refs.filter(([id]) => id === 'n25').length, 1);
});
