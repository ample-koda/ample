import test from 'node:test';
import assert from 'node:assert/strict';
import { containerView } from '../../app/graph.js';

const node = (id, ins = [], outs = []) => ({ id, title: id, tpl: 'x', x: 0, y: 0, ins, outs, flags: [] });
const flat = {
  nodes: [node('src', [], [['audio', 'audio']]), node('fx', [['in', 'audio'], ['render_in', 'render']], [['audio', 'audio'], ['render', 'render']]), node('sink', [['in', 'audio']], [])],
  links: [{ from: 'src', out: 0, to: 'fx', inp: 0, type: 'audio', outName: 'audio', inName: 'in' }],
};
const group = (extra) => ({ g: { id: 'g', title: 'g', members: ['fx'], x: 0, y: 0, ...extra } });

test('a line added by hand shows on the group even when nothing is connected to it, typed like the input or output', () => {
  const root = containerView(flat, group({ exposedOuts: [{ block: 'fx', out: 0, name: 'audio', type: 'audio' }], exposedIns: [{ block: 'fx', input: 'render_in', type: 'render' }] }), 'root');
  const g = root.nodes.find((n) => n.id === 'g');
  assert.deepEqual(g.outs.map((o) => o[1]), ['audio']);   // the output that nothing uses yet
  assert.deepEqual(g.ins.map((i) => i[1]).sort(), ['audio', 'render']);   // the real link in and the line added by hand
});

test('a link to the input of a block inside takes the line that was added for that input, and does not make a second one', () => {
  const root = containerView(flat, group({ exposedIns: [{ block: 'fx', input: 'in', type: 'audio' }] }), 'root');
  const g = root.nodes.find((n) => n.id === 'g');
  assert.equal(g.ins.length, 1);
  assert.equal(g.inBound[0].input, 'in');
  assert.equal(root.links.filter((l) => l[2] === 'g').length, 1);
});

test('the outputs of a group show the lines inside the group, with the bound ones marked so they can be removed', () => {
  const inside = containerView(flat, group({ exposedOuts: [{ block: 'fx', out: 1, name: 'render', type: 'render' }] }), 'g');
  const out = inside.nodes.find((n) => n.id === 'out');
  assert.deepEqual(out.ins.map((p) => p[1]), ['render']);
  assert.equal(out.bound[0].block, 'fx');
});

test('a line without a name of its own shows the name of the block behind it (block and line when one block has several), and the owner\'s name wins', () => {
  const g = (extra) => containerView(flat, group(extra), 'root').nodes.find((n) => n.id === 'g');
  const lines = { exposedIns: [{ block: 'fx', input: 'in', type: 'audio' }], exposedOuts: [{ block: 'fx', out: 0, name: 'audio', type: 'audio' }] };
  assert.deepEqual(g(lines).ins.map((i) => i[0]), ['fx']);
  assert.deepEqual(g(lines).outs.map((o) => o[0]), ['fx']);
  const two = { exposedIns: [{ block: 'fx', input: 'in', type: 'audio' }, { block: 'fx', input: 'render_in', type: 'render' }] };
  assert.deepEqual(g(two).ins.map((i) => i[0]).sort(), ['fx · in', 'fx · render_in']);
  assert.deepEqual(g({ ...lines, labels: { 'bind:fx:in': 'Kick in' } }).ins.map((i) => i[0]), ['Kick in']);
});
