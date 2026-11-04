import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveRollInstrument, rollConsumers } from '../roll-instrument.mjs';

const graph = {
  nodes: [{ id: 'clip', tpl: 'midi-clip' }, { id: 'up', tpl: 'transpose' }, { id: 'llama-a', tpl: 'delay-llama' }, { id: 'llama-b', tpl: 'delay-llama' }, { id: 'keys', tpl: 'piano' }, { id: 'room', tpl: 'reverb' }],
  links: [
    { from: 'clip', to: 'llama-a', type: 'midi' }, { from: 'clip', to: 'up', type: 'midi' }, { from: 'up', to: 'llama-b', type: 'midi' },
    { from: 'clip', to: 'keys', type: 'midi', disabled: true }, { from: 'llama-a', to: 'room', type: 'audio' },
  ],
};

test('a clip knows every instrument it feeds, with the midi effects in front of each', () => {
  const all = rollConsumers(graph, 'clip');
  assert.deepEqual(all.map((c) => c.node.id), ['llama-a', 'llama-b']);   // the disabled link to the piano does not count
  assert.deepEqual(all[1].chain.map((n) => n.id), ['up']);
});

test('the picked block sounds the clip, else the first', () => {
  assert.equal(resolveRollInstrument(graph, 'clip').node.id, 'llama-a');
  assert.equal(resolveRollInstrument(graph, 'clip', 'llama-b').node.id, 'llama-b');
  assert.equal(resolveRollInstrument(graph, 'clip', 'gone').node.id, 'llama-a');
  assert.equal(resolveRollInstrument(graph, 'room'), null);
});
