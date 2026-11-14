import test from 'node:test';
import assert from 'node:assert/strict';
import { removalOrder } from '../block-removal.mjs';

const link = (from, to) => ({ from, to });
const chain = [link('source', 'eq'), link('eq', 'filter')];

test('a chain is removed from its end, whatever the order of the selection (issue 93)', () => {
  const orders = [['source', 'eq', 'filter'], ['filter', 'eq', 'source'], ['eq', 'source', 'filter'], ['eq', 'filter', 'source']];
  for (const ids of orders) assert.deepEqual(removalOrder(ids, chain), ['filter', 'eq', 'source'], ids.join());
});

test('blocks that read nothing of the selection keep the order they were selected in', () => {
  assert.deepEqual(removalOrder(['b', 'a', 'c'], []), ['b', 'a', 'c']);
});

test('only the cables inside the selection count', () => {
  const links = [link('source', 'eq'), link('eq', 'outside'), link('other', 'source')];
  assert.deepEqual(removalOrder(['source', 'eq'], links), ['eq', 'source']);
  assert.deepEqual(removalOrder(['source'], links), ['source']);
});

test('a block that feeds two others goes after both', () => {
  const links = [link('s', 'a'), link('s', 'b')];
  const order = removalOrder(['s', 'a', 'b'], links);
  assert.equal(order.at(-1), 's');
  assert.deepEqual([...order].sort(), ['a', 'b', 's']);
});

test('a block twice in the selection is removed once, and a cable to itself is ignored', () => {
  assert.deepEqual(removalOrder(['a', 'a', 'b'], [link('a', 'a')]), ['a', 'b']);
});

test('blocks that read each other are all kept, in their order, at the end', () => {
  assert.deepEqual(removalOrder(['a', 'b', 'c'], [link('a', 'b'), link('b', 'a')]), ['c', 'a', 'b']);
});
