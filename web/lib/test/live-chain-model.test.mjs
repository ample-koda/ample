import test from 'node:test';
import assert from 'node:assert/strict';
import { planChain, tunable } from '../live-chain-model.mjs';

const node = (id, tpl) => ({ id, tpl, params: {} });
const link = (from, to, inName = 'in') => ({ from, to, type: 'audio', inName });
const takes = { piano: 't1', bass: 't2' };
const takeOf = (id) => takes[id];

test('a chain of effects starts at the built source and lists dependencies first', () => {
  const plan = planChain({ nodes: [node('piano', 'piano'), node('eq1', 'eq'), node('rev', 'reverb')], links: [link('piano', 'eq1'), link('eq1', 'rev')], target: 'rev', takeOf });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.map((i) => i.id), ['piano', 'eq1', 'rev']);
  assert.deepEqual(plan.sources, ['piano']); assert.equal(plan.items[0].take, 't1');
  assert.deepEqual(tunable(plan).map((i) => i.id), ['eq1', 'rev']);
});

test('a mixer sums its lanes and a shared source appears once', () => {
  const nodes = [node('piano', 'piano'), node('bass', 'bass'), node('mix', 'mixer'), node('lim', 'compressor')];
  const links = [link('piano', 'mix', 'in1'), link('bass', 'mix', 'in3'), link('mix', 'lim')];
  const plan = planChain({ nodes, links, target: 'lim', takeOf });
  assert.deepEqual(plan.items.map((i) => i.id), ['piano', 'bass', 'mix', 'lim']);
  assert.deepEqual(plan.items[2].inputs, [{ from: 'piano', lane: 0 }, { from: 'bass', lane: 2 }]);
});

test('unbuilt sources, loops and unconnected effects say why', () => {
  assert.match(planChain({ nodes: [node('drums', 'drums'), node('d', 'delay')], links: [link('drums', 'd')], target: 'd', takeOf }).reason, /Build drums first/);
  assert.match(planChain({ nodes: [node('a', 'delay'), node('b', 'delay')], links: [link('a', 'b'), link('b', 'a')], target: 'a', takeOf }).reason, /loop/);
  assert.match(planChain({ nodes: [node('d', 'delay')], links: [], target: 'd', takeOf }).reason, /no sound coming in/);
});

test('a compressor key is built into the chain as a source that drives the detector; other extra inputs are left out with a note; an unsupported effect plays its built take', () => {
  const nodes = [node('piano', 'piano'), node('bass', 'bass'), node('c', 'compressor'), node('pitch', 'pitch'), node('rev', 'reverb')];
  const withKey = planChain({ nodes, links: [link('piano', 'c'), link('bass', 'c', 'key')], target: 'c', takeOf });
  assert.equal(withKey.ok, true); assert.deepEqual(withKey.items.map((i) => i.id), ['piano', 'bass', 'c']); assert.equal(withKey.items[2].key, 'bass'); assert.deepEqual(withKey.items[2].inputs, [{ from: 'piano', lane: 0 }]);
  const gateKey = planChain({ nodes: [node('piano', 'piano'), node('bass', 'bass'), node('g', 'gate')], links: [link('piano', 'g'), link('bass', 'g', 'key')], target: 'g', takeOf });
  assert.equal(gateKey.items.length, 2); assert.match(gateKey.notes[0], /key input/);
  assert.match(planChain({ nodes: [node('piano', 'piano'), node('c', 'compressor')], links: [link('piano', 'c'), link('c', 'c', 'key')], target: 'c', takeOf }).reason, /loop/);
  const planned = planChain({ nodes, links: [link('pitch', 'rev')], target: 'rev', takeOf: (id) => (id === 'pitch' ? 't9' : null) });
  assert.deepEqual(planned.items.map((i) => i.kind), ['source', 'effect']);
});

test('built-in drums, a sampler with its notes and buffer operations on a source are made in the browser', () => {
  const nodes = [
    { id: 'beat', tpl: 'drums', params: { kit: 'builtin' } }, { id: 'clip', tpl: 'midi-clip', params: {} }, { id: 'up', tpl: 'transpose', params: {} },
    { id: 'keys', tpl: 'sampler', params: { file: 'a.wav' } }, { id: 'rev', tpl: 'reverse', params: {} }, { id: 'room', tpl: 'reverb', params: {} }, { id: 'mix', tpl: 'mixer', params: {} },
  ];
  const links = [{ from: 'clip', to: 'up', type: 'midi', inName: 'notes' }, { from: 'up', to: 'keys', type: 'midi', inName: 'notes' }, { from: 'keys', to: 'rev', ...link('keys', 'rev') }, { from: 'rev', to: 'room', ...link('rev', 'room') },
    { from: 'beat', to: 'mix', ...link('beat', 'mix', 'in1') }, { from: 'room', to: 'mix', ...link('room', 'mix', 'in2') }];
  const plan = planChain({ nodes, links, target: 'mix', takeOf: () => null });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.map((i) => `${i.id}:${i.kind}`), ['beat:local', 'keys:local', 'rev:local', 'room:effect', 'mix:sum']);
  assert.deepEqual(plan.items[1].midi, { clip: 'clip', transforms: [{ id: 'up', tpl: 'transpose' }] });
  assert.deepEqual(tunable(plan).map((i) => i.id), ['room', 'mix']);
  // a sample kit, or an operation after an effect, plays the built take instead
  const kit = planChain({ nodes: [{ id: 'beat', tpl: 'drums', params: { kit: 'kit/tape' } }], links: [], target: 'beat', takeOf: () => 't' });
  assert.equal(kit.items[0].kind, 'source');
  const afterEffect = planChain({ nodes: [{ id: 'src', tpl: 'piano', params: {} }, { id: 'e', tpl: 'eq', params: {} }, { id: 'c', tpl: 'cut', params: {} }], links: [link('src', 'e'), link('e', 'c')], target: 'c', takeOf: (id) => (id === 'c' ? 'tc' : 'ts') });
  assert.deepEqual(afterEffect.items.map((i) => i.kind), ['source']);
});
