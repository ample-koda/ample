import test from 'node:test';
import assert from 'node:assert/strict';
import { effectChainEdit } from '../effect-chain-edit.mjs';

const nodes = [
  { id: 'drums', tpl: 'drums' },
  { id: 'eq', tpl: 'eq' },
  { id: 'compressor', tpl: 'compressor' },
  { id: 'master', tpl: 'master' },
];
const templates = Object.fromEntries(nodes.map((node) => [node.tpl, {
  inputs: node.tpl === 'drums' ? [] : [{ name: 'audio', type: 'audio' }],
  outputs: node.tpl === 'master' ? [] : [{ name: 'audio', type: 'audio' }],
} ]));
templates.reverb = { inputs: [{ name: 'audio', type: 'audio' }], outputs: [{ name: 'audio', type: 'audio' }] };
const entries = nodes.slice(1, 3);
const links = [
  { from: 'drums', to: 'eq', type: 'audio', outName: 'audio', inName: 'audio' },
  { from: 'eq', to: 'compressor', type: 'audio', outName: 'audio', inName: 'audio' },
  { from: 'compressor', to: 'master', type: 'audio', outName: 'audio', inName: 'audio' },
];

test('reorders the chain while preserving its terminal connection', () => {
  const result = effectChainEdit(nodes[0], entries, links, nodes, templates, { kind: 'move', id: 'compressor', delta: -1 });
  assert.equal(result.error, undefined);
  assert.deepEqual(result.ops.filter((op) => op.type === 'connect').map(({ from, to }) => [from, to]), [['drums', 'compressor'], ['compressor', 'eq'], ['eq', 'master']]);
});

test('adds a uniquely named effect to an empty chain and preserves downstream users', () => {
  const direct = [{ from: 'drums', to: 'master', type: 'audio', outName: 'audio', inName: 'audio' }];
  const result = effectChainEdit(nodes[0], [], direct, nodes, templates, { kind: 'add', type: 'eq' });
  assert.equal(result.error, undefined);
  assert.deepEqual(result.ops.filter((op) => op.type === 'connect').map(({ from, to }) => [from, to]), [['drums', 'drums-eq'], ['drums-eq', 'master']]);
  assert.equal(result.ops[0].type, 'addBlock');
});

test('removes an effect and reconnects the remaining chain to its original consumer', () => {
  const result = effectChainEdit(nodes[0], entries, links, nodes, templates, { kind: 'remove', id: 'eq' });
  assert.equal(result.error, undefined);
  assert.deepEqual(result.ops.filter((op) => op.type === 'connect').map(({ from, to }) => [from, to]), [['drums', 'compressor'], ['compressor', 'master']]);
  assert.equal(result.ops.at(-1).type, 'removeBlock');
  assert.equal(result.ops.at(-1).block, 'eq');
});

test('uses a unique block name when adding an effect', () => {
  const occupied = [...nodes, { id: 'drums-reverb', tpl: 'reverb' }];
  const result = effectChainEdit(nodes[0], [], [{ from: 'drums', to: 'master', type: 'audio', outName: 'audio', inName: 'audio' }], occupied, templates, { kind: 'add', type: 'reverb' });
  assert.equal(result.error, undefined);
  assert.equal(result.ops[0].name, 'drums-reverb-2');
});

test('refuses an ambiguous chain instead of rewriting a branched graph', () => {
  const branched = [...links, { from: 'eq', to: 'master', type: 'audio', outName: 'audio', inName: 'audio' }];
  const result = effectChainEdit(nodes[0], entries, branched, nodes, templates, { kind: 'move', id: 'compressor', delta: -1 });
  assert.match(result.error, /separate audio consumer/);
});
