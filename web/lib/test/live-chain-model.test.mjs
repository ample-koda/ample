import test from 'node:test';
import assert from 'node:assert/strict';
import { changedSources, planChain, tunable, upstreamOf } from '../live-chain-model.mjs';

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
  // a sample kit plays the built take instead; an operation after an effect is made in the browser from the sound before it (issue 73)
  const kit = planChain({ nodes: [{ id: 'beat', tpl: 'drums', params: { kit: 'kit/tape' } }], links: [], target: 'beat', takeOf: () => 't' });
  assert.equal(kit.items[0].kind, 'source');
  const afterEffect = planChain({ nodes: [{ id: 'src', tpl: 'piano', params: {} }, { id: 'e', tpl: 'eq', params: {} }, { id: 'c', tpl: 'cut', params: {} }], links: [link('src', 'e'), link('e', 'c')], target: 'c', takeOf: (id) => (id === 'c' ? 'tc' : 'ts') });
  assert.deepEqual(afterEffect.items.map((i) => `${i.id}:${i.kind}`), ['src:source', 'e:effect', 'c:local']);   // no take of the cut is needed
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
  // after an effect these blocks are made from the rendered sound before them, not from a built take
  const after = planChain({ nodes, links: [link('piano', 'd'), link('d', 'j', 'in1'), link('j', 'r')], target: 'r', takeOf: (id) => (id === 'j' ? 'tj' : takes[id]) });
  assert.deepEqual(after.items.map((i) => [i.id, i.kind]), [['piano', 'source'], ['d', 'effect'], ['j', 'local'], ['r', 'effect']]);
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

test('a Meter between an effect and the master does not interrupt the chain: the plan looks through it (issue 75)', () => {
  const nodes = [node('click', 'click'), node('eq1', 'eq'), node('m', 'meter'), node('master', 'master')];
  const links = [link('click', 'eq1'), link('eq1', 'm', 'audio'), link('m', 'master', 'mix')];
  const plan = planChain({ nodes, links, target: 'master', takeOf: () => undefined });
  assert.equal(plan.ok, true, plan.reason);
  assert.deepEqual(plan.items.map((i) => i.id), ['click', 'eq1', 'master']);
  assert.deepEqual(plan.items.at(-1).inputs, [{ from: 'eq1', lane: 0 }]);
  assert.deepEqual(plan.sources, ['click']);
  assert.ok(!plan.items.some((i) => i.id === 'm'), 'the Meter is not an item of the audio graph');
});

test('the plan is the same with and without the Meter', () => {
  const base = [node('click', 'click'), node('eq1', 'eq'), node('master', 'master')];
  const without = planChain({ nodes: base, links: [link('click', 'eq1'), link('eq1', 'master', 'mix')], target: 'master', takeOf: () => undefined });
  const withMeter = planChain({
    nodes: [...base, node('m', 'meter')], links: [link('click', 'eq1'), link('eq1', 'm', 'audio'), link('m', 'master', 'mix')], target: 'master', takeOf: () => undefined,
  });
  assert.deepEqual(withMeter.items, without.items);
});

test('two Meters in a row are both looked through, and a Meter after a source needs no take of its own', () => {
  const nodes = [node('click', 'click'), node('m1', 'meter'), node('m2', 'meter'), node('d', 'delay')];
  const links = [link('click', 'm1', 'in'), link('m1', 'm2', 'in'), link('m2', 'd')];
  const plan = planChain({ nodes, links, target: 'd', takeOf: () => undefined });
  assert.equal(plan.ok, true, plan.reason);
  assert.deepEqual(plan.items.map((i) => i.id), ['click', 'd']);
});

test('starting the chain at a Meter starts it at the block in front of the Meter', () => {
  const nodes = [node('click', 'click'), node('eq1', 'eq'), node('m', 'meter')];
  const plan = planChain({ nodes, links: [link('click', 'eq1'), link('eq1', 'm', 'audio')], target: 'm', takeOf: () => undefined });
  assert.equal(plan.ok, true, plan.reason);
  assert.equal(plan.output, 'eq1');
  assert.deepEqual(plan.items.map((i) => i.id), ['click', 'eq1']);
});

test('a local buffer block after a Meter that follows an effect works on the sound of that effect, as it does without the Meter', () => {
  const nodes = [node('click', 'click'), node('eq1', 'eq'), node('m', 'meter'), node('norm', 'normalize'), node('master', 'master')];
  const links = [link('click', 'eq1'), link('eq1', 'm', 'audio'), link('m', 'norm', 'in'), link('norm', 'master', 'mix')];
  const plan = planChain({ nodes, links, target: 'master', takeOf: (id) => (id === 'norm' ? 'tnorm' : undefined) });
  assert.equal(plan.ok, true, plan.reason);
  assert.equal(plan.items.find((i) => i.id === 'norm').kind, 'local');
});

test('a buffer block after an effect and a mixer names the effects and mixers before it and the sounds at the edge (issue 73)', () => {
  const nodes = [node('a', 'click'), node('b', 'click'), node('eq1', 'eq'), node('mix', 'mixer'), node('norm', 'normalize'), node('rev', 'reverb'), node('master', 'master')];
  const links = [link('a', 'eq1'), link('eq1', 'mix', 'in1'), link('b', 'mix', 'in2'), link('mix', 'norm'), link('norm', 'rev'), link('rev', 'master', 'mix')];
  const plan = planChain({ nodes, links, target: 'master', takeOf: () => null });
  assert.equal(plan.ok, true, plan.reason);
  const norm = plan.items.find((i) => i.id === 'norm');
  assert.equal(norm.kind, 'local');
  const before = upstreamOf(plan, norm.inputs);
  assert.deepEqual(before.chain.map((i) => i.id), ['eq1', 'mix']);   // dependencies first
  assert.deepEqual(before.leaves.sort(), ['a', 'b']);
  assert.deepEqual(upstreamOf(plan, [{ from: 'a' }]), { chain: [], leaves: ['a'] });   // a source has nothing before it
});

test('an Autotune without a Melody input is made in the browser, with one it plays its built take (issues 78 and 81)', () => {
  const nodes = [node('voice', 'import-audio'), node('tune', 'autotune'), node('master', 'master')];
  nodes[0].params = { file: 'voice.wav' };
  const links = [link('voice', 'tune'), link('tune', 'master', 'mix')];
  const free = planChain({ nodes, links, target: 'master', takeOf: () => null });
  assert.equal(free.ok, true, free.reason);
  assert.equal(free.items.find((i) => i.id === 'tune').kind, 'local');
  const melody = { from: 'clip', to: 'tune', type: 'midi', inName: 'target' };
  const guided = planChain({ nodes: [...nodes, node('clip', 'midi-clip')], links: [...links, melody], target: 'master', takeOf: (id) => (id === 'tune' ? 'tt' : undefined) });
  assert.equal(guided.items.find((i) => i.id === 'tune').kind, 'source');
});

test('a rebuilt source is the same graph with another take, so it can be put in without starting the chain again (issue 275)', () => {
  const nodes = [node('piano', 'piano'), node('eq1', 'eq'), node('rev', 'reverb')];
  const links = [link('piano', 'eq1'), link('eq1', 'rev')];
  const first = planChain({ nodes, links, target: 'rev', takeOf: () => 'take-1' });
  const rebuilt = planChain({ nodes, links, target: 'rev', takeOf: () => 'take-2' });
  assert.deepEqual(changedSources(first, rebuilt).map((i) => [i.id, i.take]), [['piano', 'take-2']]);
  assert.deepEqual(changedSources(first, first), []);
  // another graph (a block was added) is a restart, not an adoption
  const more = planChain({ nodes: [...nodes, node('d', 'delay')], links: [...links, link('rev', 'd')], target: 'd', takeOf: () => 'take-2' });
  assert.equal(changedSources(first, more), null);
});

test('a processed loop seam is a chain: body and region are two sounds of one file, the seam goes through an effect, and a region merge adds them (issue 79)', () => {
  const nodes = [{ id: 'loop', tpl: 'import-audio', params: { file: 'loop.wav' } }, node('duck', 'duck'), node('merge', 'region-merge'), node('out', 'master')];
  const links = [
    { from: 'loop', to: 'duck', type: 'region', inName: 'region_in', outName: 'loop-region' },
    { from: 'loop', to: 'merge', type: 'audio', inName: 'body', outName: 'body' },
    { from: 'duck', to: 'merge', type: 'region', inName: 'region', outName: 'region' },
    { from: 'merge', to: 'out', type: 'audio', inName: 'mix', outName: 'audio' },
  ];
  const plan = planChain({ nodes, links, target: 'out', takeOf });
  assert.equal(plan.ok, true, plan.reason);
  assert.deepEqual(plan.items.map((i) => i.id), ['loop:body', 'loop:region', 'duck', 'merge', 'out']);
  assert.deepEqual(plan.items[1], { id: 'loop:region', kind: 'local', tpl: 'import-audio', inputs: [], base: 'loop', port: 'region' });
  assert.equal(plan.items[0].port, 'body');
  assert.deepEqual(plan.items[2].inputs, [{ from: 'loop:region', lane: 0 }]);   // the region goes through the effect as the sound it is
  assert.deepEqual(plan.items[3].inputs, [{ from: 'loop:body', lane: 0 }, { from: 'duck', lane: 1 }]);   // body on lane 0, the region on lane 1
  assert.deepEqual(upstreamOf(plan, [{ from: 'merge' }]).leaves.sort(), ['loop:body', 'loop:region']);
});

test('the seam alone can be merged, and a loop without a file of the project has no ports to hear', () => {
  const nodes = [{ id: 'loop', tpl: 'import-audio', params: { file: 'loop.wav' } }, node('merge', 'region-merge')];
  const alone = planChain({ nodes, links: [{ from: 'loop', to: 'merge', type: 'region', inName: 'region', outName: 'loop-region' }], target: 'merge', takeOf });
  assert.equal(alone.ok, true, alone.reason);
  assert.deepEqual(alone.items.at(-1).inputs, [{ from: 'loop:region', lane: 1 }]);
  const empty = planChain({ nodes: [{ id: 'loop', tpl: 'import-audio', params: {} }, node('merge', 'region-merge')], links: [{ from: 'loop', to: 'merge', type: 'region', inName: 'region', outName: 'loop-region' }], target: 'merge', takeOf });
  assert.match(empty.reason, /no file the browser can read/);
});

test('the plain audio of a loop is still one sound under its own id', () => {
  const plan = planChain({ nodes: [{ id: 'loop', tpl: 'import-audio', params: { file: 'loop.wav' } }, node('eq1', 'eq')], links: [{ from: 'loop', to: 'eq1', type: 'audio', inName: 'in', outName: 'audio' }], target: 'eq1', takeOf });
  assert.deepEqual(plan.items[0], { id: 'loop', kind: 'local', tpl: 'import-audio', inputs: [] });
});

test('effects chained by their render ports work on one sound: eq -> pan -> bus -> reverb -> master of a chamber song', () => {
  const nodes = [{ id: 'score', tpl: 'sequencer', params: {} }, node('tone', 'eq'), node('seat', 'pan'), node('ens', 'bus'), node('chamber', 'reverb'), node('master', 'master')];
  const links = [
    { from: 'score', to: 'tone', type: 'audio', inName: 'in', outName: 'lane1' },
    { from: 'tone', to: 'seat', type: 'render', inName: 'render', outName: 'render' },
    { from: 'seat', to: 'ens', type: 'audio', inName: 'in1', outName: 'audio' },
    { from: 'ens', to: 'chamber', type: 'audio', inName: 'in', outName: 'audio' },
    { from: 'chamber', to: 'master', type: 'audio', inName: 'mix', outName: 'audio' },
  ];
  const plan = planChain({ nodes, links, target: 'master', takeOf: (id) => (id === 'score' ? 't1' : null) });
  assert.equal(plan.ok, true, plan.reason);
  const order = plan.items.map((i) => i.id);
  assert.ok(order.indexOf('tone') < order.indexOf('seat') && order.indexOf('seat') < order.indexOf('ens'), order.join(' '));
  assert.deepEqual(plan.items.find((i) => i.id === 'seat').inputs, [{ from: 'tone', lane: 0 }]);
});
