import test from 'node:test';
import assert from 'node:assert/strict';
import { describeChanges, groupChanges } from '../koda-diff.mjs';

const song = (a, b) => `reverb reverb {\n  wet ${a}\n}\n\nmixer master {\n  gain ${b}\n}\n`;

test('the changes of a draft are grouped per block, in the order the blocks appear', () => {
  const items = describeChanges(song(20, 0), song(40, -1));
  const groups = groupChanges(items);
  assert.deepEqual(groups.map((g) => g.block), ['reverb', 'master']);
  assert.ok(groups.every((g) => g.kind === 'changed' && g.items.length === 1));
});

test('a block that is new or gone gets its own kind', () => {
  const added = `${song(20, 0)}\ndelay echo {\n  time 3\n}\n`;
  const withNew = groupChanges(describeChanges(song(20, 0), added));
  assert.equal(withNew.length, 1);
  assert.deepEqual([withNew[0].block, withNew[0].kind], ['echo', 'new']);
  const gone = groupChanges(describeChanges(added, song(20, 0)));
  assert.deepEqual([gone[0].block, gone[0].kind], ['echo', 'gone']);
});

test('items without a block stay together, and the closing "and N more" joins the last group', () => {
  const groups = groupChanges([{ type: 'add', text: 'a' }, { type: 'change', text: 'b', block: 'x' }, { type: 'change', text: 'and 2 more' }]);
  assert.deepEqual(groups.map((g) => [g.block, g.items.length]), [['', 1], ['x', 2]]);
});
