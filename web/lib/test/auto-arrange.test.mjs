import test from 'node:test';
import assert from 'node:assert/strict';
import { autoArrange } from '../auto-arrange.mjs';

const node = (id, x, y, w = 300, h = 100) => ({ id, x, y, w, h });

test('what feeds a block is left of it, and the group keeps its top left corner', () => {
  const nodes = [node('c', 500, 40), node('a', 900, 300), node('b', 10, 800)];
  const links = [{ from: 'a', to: 'b' }, { from: 'b', to: 'c' }];
  const at = autoArrange(nodes, links, ['a', 'b', 'c']);
  assert.ok(at.get('a').x < at.get('b').x && at.get('b').x < at.get('c').x);
  assert.equal(Math.min(...[...at.values()].map((p) => p.x)), 10);   // the left edge it had
  assert.equal(Math.min(...[...at.values()].map((p) => p.y)), 40);   // the top it had
});

test('two blocks that feed one block are in the same column, and the rows follow the blocks that feed them', () => {
  const nodes = [node('a', 0, 0), node('b', 0, 400), node('m', 0, 200), node('x', 0, 0), node('y', 0, 0)];
  const links = [{ from: 'a', to: 'm' }, { from: 'b', to: 'm' }, { from: 'm', to: 'x' }, { from: 'm', to: 'y' }];
  const at = autoArrange(nodes, links, ['a', 'b', 'm', 'x', 'y']);
  assert.equal(at.get('a').x, at.get('b').x);
  assert.equal(at.get('x').x, at.get('y').x);
  assert.ok(at.get('m').x > at.get('a').x && at.get('x').x > at.get('m').x);
});

test('blocks that are not linked to the others go in a grid below, and blocks that are not selected stay out', () => {
  const nodes = [node('a', 0, 0), node('b', 0, 0), node('l1', 50, 50), node('l2', 60, 60), node('far', 5000, 5000)];
  const at = autoArrange(nodes, [{ from: 'a', to: 'b' }], ['a', 'b', 'l1', 'l2']);
  assert.ok(at.get('l1').y > at.get('a').y + 100);
  assert.equal(at.get('l1').y, at.get('l2').y);
  assert.ok(!at.has('far'));
});

test('a loop does not hang and fewer than two blocks are left alone', () => {
  const nodes = [node('a', 0, 0), node('b', 0, 0)];
  assert.equal(autoArrange(nodes, [{ from: 'a', to: 'b' }, { from: 'b', to: 'a' }], ['a', 'b']).size, 2);
  assert.equal(autoArrange(nodes, [], ['a']).size, 0);
});

test('a block marked last ends in the last column even when its path is short', () => {
  const nodes = [node('in1', 0, 0), node('fx', 0, 0), node('out2', 0, 0), { ...node('master', 0, 0), last: true }, node('in2', 0, 0)];
  const links = [{ from: 'in1', to: 'fx' }, { from: 'fx', to: 'out2' }, { from: 'in2', to: 'master' }, { from: 'out2', to: 'master' }];
  const at = autoArrange(nodes, links, nodes.map((n) => n.id));
  assert.equal(Math.max(...[...at.values()].map((p) => p.x)), at.get('master').x);
  assert.equal(at.get('in1').x, at.get('in2').x);   // the inputs are on the left together
});

test('rows are ordered so that the links of a column do not cross', () => {
  const nodes = [node('a', 0, 0), node('b', 0, 200), node('c', 0, 400), node('x', 0, 0), node('y', 0, 200), node('z', 0, 400)];
  // a feeds z, b feeds y, c feeds x: straight across means reversing the second column
  const at = autoArrange(nodes, [{ from: 'a', to: 'z' }, { from: 'b', to: 'y' }, { from: 'c', to: 'x' }], nodes.map((n) => n.id));
  assert.ok(at.get('a').y < at.get('b').y && at.get('b').y < at.get('c').y);
  assert.ok(at.get('z').y < at.get('y').y && at.get('y').y < at.get('x').y);
});

test('a pinned block stays where it is and the others are placed around it', () => {
  const nodes = [node('a', 0, 0), node('b', 0, 300), node('c', 0, 600), node('p', 400, 0)];   // p is pinned in the place where the second column would start
  const links = [{ from: 'a', to: 'b' }, { from: 'b', to: 'c' }];
  const at = autoArrange(nodes, links, nodes.map((n) => n.id), ['p']);
  assert.ok(!at.has('p'), 'a pinned block is not given a new place');
  for (const id of ['a', 'b', 'c']) {
    const n = nodes.find((x) => x.id === id), p = at.get(id);
    const hit = p.x < 400 + 300 + 20 && 400 < p.x + n.w + 20 && p.y < 0 + 100 + 20 && 0 < p.y + n.h + 20;
    assert.ok(!hit, `${id} does not lie on the pinned block`);
  }
});

test('with one pinned block the others of a selection are still arranged, and fewer than two movable blocks without a pin are left alone', () => {
  const nodes = [node('a', 0, 0), node('b', 500, 500), node('p', 900, 0)];
  assert.equal(autoArrange(nodes, [{ from: 'a', to: 'b' }], ['a', 'b', 'p'], ['p']).size, 2);
  assert.equal(autoArrange(nodes, [], ['p', 'a'], ['p', 'a']).size, 0);
});

test('a link that jumps over blocks gets a lane of its own: the blocks in between go below it', () => {
  const box = (id, x) => ({ id, x, y: 100, w: 220, h: 120 });
  const nodes = [box('rewind', 0), box('echo', 300), box('filter', 600), box('merge', 900)];
  const links = [{ from: 'rewind', to: 'echo' }, { from: 'echo', to: 'filter' }, { from: 'filter', to: 'merge' }, { from: 'rewind', to: 'merge' }];
  const placed = autoArrange(nodes, links, nodes.map((n) => n.id));
  for (const id of ['echo', 'filter']) assert.ok(placed.get(id).y >= placed.get('rewind').y + 120, id + ' is below the jumped link');
  assert.equal(placed.get('merge').y, placed.get('rewind').y);
});

test('top to bottom: what feeds a block is above it, in rows, and the group keeps its corner', () => {
  const nodes = [node('c', 500, 40), node('a', 900, 300), node('b', 10, 800)];
  const links = [{ from: 'a', to: 'b' }, { from: 'b', to: 'c' }];
  const at = autoArrange(nodes, links, ['a', 'b', 'c'], [], { mode: 'ttb' });
  assert.ok(at.get('a').y < at.get('b').y && at.get('b').y < at.get('c').y);
  assert.equal(at.get('a').x, at.get('b').x);   // one lane
  assert.equal(Math.min(...[...at.values()].map((p) => p.x)), 10);
  assert.equal(Math.min(...[...at.values()].map((p) => p.y)), 40);
});

test('in rows: a long chain folds into bands below each other instead of one very wide row', () => {
  const ids = Array.from({ length: 8 }, (_, i) => `n${i}`);
  const nodes = ids.map((id) => node(id, 0, 0, 300, 100));
  const links = ids.slice(1).map((id, i) => ({ from: ids[i], to: id }));
  const flat = autoArrange(nodes, links, ids);
  const wrapped = autoArrange(nodes, links, ids, [], { mode: 'wrap' });
  const width = (m) => Math.max(...[...m.values()].map((p) => p.x)) - Math.min(...[...m.values()].map((p) => p.x));
  assert.ok(width(wrapped) < width(flat));
  assert.ok(Math.max(...[...wrapped.values()].map((p) => p.y)) > Math.max(...[...flat.values()].map((p) => p.y)));   // a second band below
  assert.equal(new Set([...wrapped.values()].map((p) => `${p.x},${p.y}`)).size, 8);   // nothing on top of anything
});
