import test from 'node:test';
import assert from 'node:assert/strict';
import { HEAD, ROW, autoLayout, bounds, compatible, fitPhoneView, fitReadableView, fitView, nodesInRect, snap, zoomAt, align, distribute, linkPath, nodeHeight, nodeWidth, portPos, W } from '../../app/geometry.js';
import { connectedEffectChain, containerView, flatGraph, groupBlocks, pruneGroups, ungroup } from '../../app/graph.js';

const templates = {
  src: { class: 'stable', version: '1.0', dynamic: false, plugin: null, inputs: [], outputs: [{ name: 'out', type: 'midi' }] },
  mid: { class: 'stable', version: '1.0', dynamic: false, plugin: null, inputs: [{ name: 'in', type: 'midi' }], outputs: [{ name: 'out', type: 'audio' }] },
  end: { class: 'live', version: '2.0', dynamic: true, plugin: 'p', inputs: [{ name: 'in', type: 'audio' }], outputs: [] },
};
const doc = {
  blocks: [
    { name: 'a', type: 'src', parameters: [{ name: 'bars', value: '4' }], flags: [], froms: [], parts: [] },
    { name: 'b', type: 'mid', parameters: [], flags: [], froms: [{ block: 'a', output: 'out', input: 'in' }], parts: [] },
    { name: 'c', type: 'end', parameters: [], flags: ['bake'], froms: [{ block: 'b', output: 'out', input: 'in' }], parts: [], layout: { x: 500, y: 40 } },
  ],
};

test('types are compatible when equal or any', () => {
  assert.ok(compatible('audio', 'audio'));
  assert.ok(compatible('any', 'midi'));
  assert.ok(compatible('text', 'any'));
  assert.ok(!compatible('audio', 'midi'));
});

test('connected audio effects are listed in signal order and ignore unrelated branches', () => {
  const nodes = [{ id: 'voice', tpl: 'ai-voice' }, { id: 'eq', tpl: 'eq' }, { id: 'comp', tpl: 'compressor' }, { id: 'mixer', tpl: 'mixer' }, { id: 'lyrics', tpl: 'lyrics' }];
  const links = [{ from: 'voice', to: 'eq', type: 'audio' }, { from: 'eq', to: 'comp', type: 'audio' }, { from: 'comp', to: 'mixer', type: 'audio' }, { from: 'voice', to: 'lyrics', type: 'text' }];
  const effects = new Set(['eq', 'filter', 'compressor', 'duck', 'distortion', 'delay', 'reverb']);
  assert.deepEqual(connectedEffectChain('voice', nodes, links, effects).map((node) => node.id), ['eq', 'comp']);
  assert.deepEqual(connectedEffectChain('eq', nodes, links, effects).map((node) => node.id), ['comp']);
  assert.ok(nodeHeight({ outs: [['audio', 'audio']] }) > nodeHeight({ outs: [] }));
});

test('snap rounds to the grid', () => {
  assert.equal(snap(14), 10);
  assert.equal(snap(15), 20);
});

test('zoom keeps the point under the pointer still and respects the limits', () => {
  const v = zoomAt({ x: 20, y: 10, k: 1 }, 100, 80, 2);
  assert.equal(v.k, 2);
  assert.equal((100 - v.x) / v.k, 100 - 20);
  assert.equal(zoomAt({ x: 0, y: 0, k: 2.4 }, 0, 0, 10).k, 2.5);
  assert.equal(zoomAt({ x: 0, y: 0, k: 0.21 }, 0, 0, 0.01).k, 0.2);
});

test('auto layout puts a block right of what feeds it, deterministically', () => {
  const l = autoLayout(doc.blocks, templates);
  assert.ok(l.get('b').x > l.get('a').x);
  assert.equal(l.get('b').x - l.get('a').x, 372);
  assert.ok(l.get('c').x > l.get('b').x);
  assert.deepEqual([...autoLayout(doc.blocks, templates)], [...l]);
});

test('auto layout sizes columns to their widest node and stacks cards without overlap', () => {
  const blocks = [{ name: 'source', type: 'src', froms: [] }, { name: 'mixer', type: 'mixer', froms: [{ block: 'source' }] }, { name: 'other', type: 'mixer', froms: [{ block: 'source' }] }];
  const layoutTemplates = { src: { inputs: [], outputs: [{ name: 'audio', type: 'audio' }] }, mixer: { inputs: [{ name: 'first-input', type: 'audio' }, { name: 'second-input', type: 'audio' }], outputs: [{ name: 'output', type: 'audio' }] } };
  const layout = autoLayout(blocks, layoutTemplates);
  const mixer = { title: 'mixer', ins: layoutTemplates.mixer.inputs, outs: layoutTemplates.mixer.outputs };
  assert.equal(layout.get('mixer').x - layout.get('source').x, nodeWidth(mixer) + 72);
  assert.ok(layout.get('other').y >= layout.get('mixer').y + nodeHeight(mixer) + 24);
});

test('auto layout keeps a lane free above the cards that a longer line passes over', () => {
  const lt = { src: { inputs: [], outputs: [{ name: 'audio', type: 'audio' }] }, fx: { inputs: [{ name: 'audio', type: 'audio' }], outputs: [{ name: 'audio', type: 'audio' }] } };
  const blocks = [{ name: 'a', type: 'src', froms: [] }, { name: 'b', type: 'fx', froms: [{ block: 'a' }] }, { name: 'c', type: 'fx', froms: [{ block: 'b' }] }, { name: 'd', type: 'fx', froms: [{ block: 'a' }, { block: 'c' }] }];
  const l = autoLayout(blocks, lt);
  assert.ok(l.get('b').y > l.get('a').y + 100 && l.get('c').y > l.get('a').y + 100);   // b and c sit under the line from a to d
  assert.equal(l.get('d').y, l.get('a').y);
});

test('auto layout survives a cycle and a missing source', () => {
  const l = autoLayout([{ name: 'x', froms: [{ block: 'y' }] }, { name: 'y', froms: [{ block: 'x' }] }, { name: 'z', froms: [{ block: 'ghost' }] }], {});
  assert.equal(l.size, 3);
});

test('flat graph resolves ports by name and keeps the layout lines', () => {
  const g = flatGraph(doc, templates);
  assert.equal(g.nodes.length, 3);
  assert.equal(g.links.length, 2);
  assert.equal(g.nodes[2].x, 500);
  assert.equal(g.nodes[2].placed, true);
  assert.equal(g.nodes[0].placed, false);
  assert.equal(g.links[0].type, 'midi');
  assert.equal(g.nodes[2].dyn, true);
});

test('flat graph adds module instances with typed ports and enter metadata', () => {
  const source = { blocks: [doc.blocks[0], { ...doc.blocks[2], froms: [{ block: 'groove', output: 'audio', input: 'in' }] }], uses: [{ module: 'generated', version: '1.0', instance: 'groove', line: 3, parameters: [], inputs: [{ name: 'notes', type: 'midi' }], outputs: [{ name: 'audio', type: 'audio' }], froms: [{ block: 'a', output: 'out', input: 'notes' }] }] };
  const graph = flatGraph(source, templates);
  const module = graph.nodes.find((n) => n.isModule);
  assert.equal(module.id, 'groove');
  assert.equal(module.moduleName, 'generated');
  assert.deepEqual(module.ins, [['notes', 'midi']]);
  assert.deepEqual(module.outs, [['audio', 'audio']]);
  assert.equal(graph.links.length, 2);
});

test('a connection to an unknown port is left out', () => {
  const bad = { blocks: [{ name: 'a', type: 'src', parameters: [], flags: [], froms: [], parts: [] }, { name: 'b', type: 'mid', parameters: [], flags: [], froms: [{ block: 'a', output: 'nope', input: 'in' }], parts: [] }] };
  assert.equal(flatGraph(bad, templates).links.length, 0);
});

test('grouping turns crossing links into group ports and ungrouping restores the view', () => {
  const flat = flatGraph(doc, templates);
  const r = groupBlocks(flat, {}, ['b', 'c']);
  assert.ok(!r.error);
  const root = containerView(flat, r.groups, 'root');
  assert.deepEqual(root.nodes.map((n) => n.id).sort(), ['a', r.id].sort());
  assert.equal(root.links.length, 1);
  const group = root.nodes.find((n) => n.isGroup);
  assert.equal(group.ins.length, 1);
  assert.equal(group.outs.length, 0);
  const inner = containerView(flat, r.groups, r.id);
  assert.deepEqual(inner.nodes.filter((n) => !n.proxy).map((n) => n.id), ['b', 'c']);
  assert.equal(inner.links.length, 2);
  const back = containerView(flat, ungroup(r.groups, r.id), 'root');
  assert.equal(back.nodes.length, 3);
  assert.equal(back.links.length, 2);
});

test('grouping refuses fewer than two free blocks', () => {
  const flat = flatGraph(doc, templates);
  assert.ok(groupBlocks(flat, {}, ['a']).error);
  const r = groupBlocks(flat, {}, ['a', 'b']);
  assert.ok(groupBlocks(flat, r.groups, ['a', 'c']).error);
});

test('groups lose members that left the source', () => {
  const flat = flatGraph(doc, templates);
  const r = groupBlocks(flat, {}, ['a', 'b']);
  const smaller = flatGraph({ blocks: doc.blocks.slice(1) }, templates);
  assert.deepEqual(pruneGroups(r.groups, smaller), {});
});

test('rubber band, align and distribute', () => {
  const nodes = [{ id: 'a', x: 0, y: 0, ins: [], outs: [] }, { id: 'b', x: 300, y: 100, ins: [], outs: [] }, { id: 'c', x: 700, y: 300, ins: [], outs: [] }];
  assert.deepEqual(nodesInRect(nodes, { x: -10, y: -10 }, { x: 350, y: 150 }).map((n) => n.id), ['a', 'b']);
  assert.equal(align(nodes, 'y').get('c').y, 0);
  assert.equal(distribute(nodes, 'x').get('b').x, 350);
  assert.equal(distribute(nodes.slice(0, 2), 'x').size, 0);
});

test('fit view shows every node', () => {
  const nodes = [{ id: 'a', x: 0, y: 0, ins: [], outs: [] }, { id: 'b', x: 1000, y: 600, ins: [], outs: [] }];
  const v = fitView(nodes, { width: 800, height: 500 });
  assert.ok(v.k >= 0.2 && v.k <= 1.5);
  assert.ok(v.x >= 0 && v.x + (1000 + W) * v.k <= 801);
});

test('initial fit keeps the graph readable while centering it', () => {
  const nodes = [{ id: 'a', x: 100, y: 50, title: 'Drums', ins: [], outs: [] }, { id: 'b', x: 600, y: 250, title: 'Compressor', ins: [['audio', 'audio']], outs: [['audio', 'audio']] }];
  const view = fitReadableView(nodes, { width: 900, height: 600 });
  const graph = bounds(nodes);
  assert.ok(view.k > .75 && view.k <= 1);
  assert.ok(Math.abs(view.x + (graph.x0 + graph.x1) * view.k / 2 - 450) < 1);
  assert.ok(Math.abs(view.y + (graph.y0 + graph.y1) * view.k / 2 - 300) < 1);
});

test('initial framing keeps a wide graph readable and anchors its first nodes for panning', () => {
  const nodes = [{ id: 'a', x: 0, y: 0, ins: [], outs: [] }, { id: 'b', x: 1800, y: 900, ins: [], outs: [] }];
  const size = { width: 1000, height: 700 };
  const view = fitReadableView(nodes, size);
  const graph = bounds(nodes);
  assert.equal(view.k, .8);
  assert.equal(view.x + graph.x0 * view.k, 20);
  assert.ok(view.x + graph.x1 * view.k > size.width);
  assert.ok(Math.abs(view.y + (graph.y0 + graph.y1) * view.k / 2 - size.height / 2) < 1);
});

test('port positions and link paths', () => {
  const node = { x: 10, y: 20, title: 'Compressor', ins: [['audio', 'audio']], outs: [['audio', 'audio']] };
  assert.deepEqual(portPos(node, 'out', 1), { x: 10 + nodeWidth(node), y: 20 + HEAD + ROW + ROW / 2 });
  assert.match(linkPath({ x: 0, y: 0 }, { x: 100, y: 50 }), /^M0 0 C /);
});

test('wide asymmetric port labels receive enough room for their own column', () => {
  const node = { title: 'Audio', ins: [['exceptionally-long-audio-input-label-continues-with-detail', 'audio']], outs: [['out', 'audio']] };
  assert.equal(nodeWidth(node), 680);
});

test('ordinary block cards start wide enough for titles and full port labels', () => {
  assert.equal(nodeWidth({ title: 'Short', ins: [], outs: [] }), 300);
  assert.ok(nodeWidth({ title: 'Block', ins: [['midi-event-source-with-detail', 'midi']], outs: [['rendered-audio-output', 'audio']] }) >= 300);
});

test('effect card title width preserves room for its shortcut, source and Advanced icons', () => {
  assert.ok(nodeWidth({ title: 'Compressor', tpl: 'compressor', ins: [], outs: [] }) >= 290);
});

test('a phone starts at a readable zoom at the top left, a wide screen and a small graph get the plain fit', () => {
  const nodes = [{ id: 'a', x: 40, y: 30, ins: [], outs: [] }, { id: 'b', x: 3000, y: 2000, ins: [], outs: [] }];
  const phone = fitPhoneView(nodes, { width: 390, height: 700 }, 60, 44);
  assert.equal(phone.k, 0.7);
  assert.equal(phone.x + 40 * phone.k, 16);
  assert.equal(phone.y + 30 * phone.k, 44 + 16);
  assert.deepEqual(fitPhoneView(nodes, { width: 1200, height: 800 }, 60, 44), fitView(nodes, { width: 1200, height: 800 }, 60, 44));
  const small = [{ id: 'a', x: 0, y: 0, ins: [], outs: [] }, { id: 'b', x: 200, y: 100, ins: [], outs: [] }];
  assert.deepEqual(fitPhoneView(small, { width: 390, height: 700 }, 60, 44), fitView(small, { width: 390, height: 700 }, 60, 44));
});
