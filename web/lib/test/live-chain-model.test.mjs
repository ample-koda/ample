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

test('click, join, crossfade and splitter are made in the browser (plan 2.5)', () => {
  const nodes = [node('piano', 'piano'), node('bass', 'bass'), node('tick', 'click'), node('j', 'join'), node('x', 'crossfade'), node('s', 'splitter'), node('d', 'delay'), node('r', 'reverb')];
  const joined = planChain({ nodes, links: [link('piano', 'j', 'in1'), link('bass', 'j', 'in3'), link('j', 'd')], target: 'd', takeOf });
  assert.equal(joined.ok, true);
  assert.deepEqual(joined.items.map((i) => [i.id, i.kind, i.tpl]), [['piano', 'source', 'piano'], ['bass', 'source', 'bass'], ['j', 'local', 'join'], ['d', 'effect', 'delay']]);
  assert.deepEqual(joined.items[2].inputs, [{ from: 'piano', lane: 0 }, { from: 'bass', lane: 2 }]);
  const crossed = planChain({ nodes, links: [link('bass', 'x', 'b'), link('piano', 'x', 'a'), link('x', 'd')], target: 'd', takeOf });
  assert.deepEqual(crossed.items.find((i) => i.id === 'x').inputs.sort((a, b) => a.lane - b.lane), [{ from: 'piano', lane: 0 }, { from: 'bass', lane: 1 }]);
  const split = planChain({ nodes, links: [link('piano', 's', 'in'), { ...link('s', 'd'), outName: 'right' }, { ...link('s', 'r'), outName: 'left' }, link('d', 'r')], target: 'r', takeOf });
  assert.equal(split.ok, true);
  assert.deepEqual(split.items.filter((i) => i.tpl === 'splitter').map((i) => [i.id, i.base, i.port]).sort(), [['s:left', 's', 'left'], ['s:right', 's', 'right']]);
  assert.deepEqual(planChain({ nodes, links: [link('tick', 'd')], target: 'd', takeOf }).items.map((i) => [i.id, i.kind]), [['tick', 'local'], ['d', 'effect']]);
  // after an effect these blocks play their built take instead
  const after = planChain({ nodes, links: [link('piano', 'd'), link('d', 'j', 'in1'), link('j', 'r')], target: 'r', takeOf: (id) => (id === 'j' ? 'tj' : takes[id]) });
  assert.deepEqual(after.items.map((i) => [i.id, i.kind]), [['j', 'source'], ['r', 'effect']]);
  assert.match(planChain({ nodes: [node('imp', 'import-audio'), node('d', 'delay')], links: [link('imp', 'd')], target: 'd', takeOf }).reason, /Build imp first/);   // no file: the built take or nothing
});

test('a send has two outputs in the chain: through and send (plan 2.6)', () => {
  const nodes = [node('piano', 'piano'), node('bass', 'bass'), node('snd', 'send'), node('d', 'delay'), node('mix', 'mixer')];
  const links = [link('piano', 'snd', 'in1'), link('bass', 'snd', 'in2'), { ...link('snd', 'd'), outName: 'send' }, { ...link('snd', 'mix', 'in1'), outName: 'through' }, link('d', 'mix', 'in2')];
  const plan = planChain({ nodes, links, target: 'mix', takeOf });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.map((i) => i.id), ['piano', 'bass', 'snd', 'd', 'mix']);   // the send is planned once
  assert.deepEqual(plan.items.find((i) => i.id === 'd').inputs, [{ from: 'snd:send', lane: 0 }]);
  assert.deepEqual(plan.items.find((i) => i.id === 'mix').inputs, [{ from: 'snd', lane: 0 }, { from: 'd', lane: 1 }]);
});

test('synth, piano, bass and guitar that play a midi clip are made in the browser; humanize or a missing clip use the built take (plan 3.4, 3.5)', () => {
  const nodes = [{ id: 'clip', tpl: 'midi-clip', params: {} }, { id: 'lead', tpl: 'synth', params: {} }, { id: 'keys', tpl: 'piano', params: { humanize: '10' } }, { id: 'low', tpl: 'bass', params: {} }, { id: 'loose', tpl: 'synth', params: {} }, { id: 'room', tpl: 'reverb', params: {} }];
  const notes = (to) => ({ from: 'clip', to, type: 'midi', inName: 'notes' });
  const lead = planChain({ nodes, links: [notes('lead'), link('lead', 'room')], target: 'room', takeOf });
  assert.deepEqual(lead.items.map((i) => [i.id, i.kind, i.voice || '']), [['lead', 'local', 'synth'], ['room', 'effect', '']]);
  assert.deepEqual(lead.items[0].midi, { clip: 'clip', transforms: [] });
  assert.match(planChain({ nodes, links: [notes('keys'), link('keys', 'room')], target: 'room', takeOf }).reason, /Build keys first/);   // humanize: the server's render
  assert.equal(planChain({ nodes, links: [notes('low'), link('low', 'room')], target: 'room', takeOf }).items[0].voice, 'bass');
  assert.match(planChain({ nodes, links: [link('loose', 'room')], target: 'room', takeOf }).reason, /Build loose first/);   // no clip feeds it
});

test('a convolution block is an effect of the chain (its impulse is loaded by the chain)', () => {
  const plan = planChain({ nodes: [node('piano', 'piano'), node('room', 'convolution')], links: [link('piano', 'room')], target: 'room', takeOf });
  assert.equal(plan.ok, true); assert.deepEqual(plan.items.map((i) => i.kind), ['source', 'effect']);
});

test('an inline sequencer is made in the browser, one item per used output; a file or other voices use the built take (plan 3.6)', () => {
  const seq = 'sequencer 1\nchannel lead notes wave=square\nchannel bass bass wave=saw\npattern p bars=1\n  lead 1:0:1:60:96\n  bass 1:0:2:36:90\nplaylist\n  clip p track=1 at=1 for=1\n';
  const nodes = [{ id: 'sq', tpl: 'sequencer', params: { sequence: seq, lanes: 'lane1=lead lane2=bass' } }, { id: 'room', tpl: 'reverb', params: {} }, { id: 'mix', tpl: 'mixer', params: {} }];
  const plan = planChain({ nodes, links: [{ ...link('sq', 'room'), outName: 'lane2' }, { ...link('sq', 'mix', 'in1'), outName: 'lane1' }, link('room', 'mix', 'in2')], target: 'mix', takeOf });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.filter((i) => i.tpl === 'sequencer').map((i) => [i.id, i.base, i.port]).sort(), [['sq:lane1', 'sq', 'lane1'], ['sq:lane2', 'sq', 'lane2']]);
  const fileOnly = [{ id: 'sq', tpl: 'sequencer', params: { file: 'sequences/a.seq' } }, nodes[1]];
  assert.match(planChain({ nodes: fileOnly, links: [link('sq', 'room')], target: 'room', takeOf }).reason, /Build sq first/);
});

test('a vocoder with a voice and a carrier is made in the browser (plan 4.5)', () => {
  const nodes = [node('piano', 'piano'), node('bass', 'bass'), node('v', 'vocoder'), node('room', 'reverb')];
  const plan = planChain({ nodes, links: [link('piano', 'v', 'modulator'), link('bass', 'v', 'carrier'), link('v', 'room')], target: 'room', takeOf });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.find((i) => i.id === 'v').inputs, [{ from: 'piano', lane: 0 }, { from: 'bass', lane: 1 }]);
  assert.equal(plan.items.find((i) => i.id === 'v').kind, 'local');
});

test('a lane of a built sequencer plays its own audio of the take (role), not the mix of all lanes', () => {
  const nodes = [{ id: 'score', tpl: 'sequencer', params: { file: 'a.ampleseq' } }, node('vol', 'volume')];
  const plan = planChain({ nodes, links: [{ from: 'score', to: 'vol', type: 'audio', inName: 'in', outName: 'lane2' }], target: 'vol', takeOf: (id) => (id === 'score' ? 'ts' : null) });
  assert.equal(plan.ok, true);
  assert.equal(plan.items[0].id, 'score:lane2'); assert.equal(plan.items[0].take, 'ts'); assert.equal(plan.items[0].role, 'lane2');
  const mix = planChain({ nodes, links: [link('score', 'vol')], target: 'vol', takeOf: (id) => (id === 'score' ? 'ts' : null) });
  assert.equal(mix.items[0].role, undefined);
});
