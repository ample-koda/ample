import test from 'node:test';
import assert from 'node:assert/strict';
import { convolutionImpulse, distortionCurve, duckAutomationEvents, effectGraph, fetchTakeAudio, liveEffectGraph } from '../../app/studio/preview/audio.js';

function fakeContext() {
  const nodes = [];
  const create = (kind) => {
    const parameter = { value: 1, events: [], cancelScheduledValues() {}, setTargetAtTime(value, time) { this.value = value; this.events.push({ value, time }); }, setValueAtTime(value, time) { this.value = value; this.events.push({ value, time }); }, linearRampToValueAtTime(value, time) { this.value = value; this.events.push({ value, time }); } };
    const node = { kind, connections: [], connect(target) { this.connections.push(target); return target; }, disconnect() {}, gain: { ...parameter, events: [] }, frequency: { ...parameter, events: [] }, Q: { ...parameter, events: [] }, threshold: { ...parameter, events: [] }, ratio: { ...parameter, events: [] }, attack: { ...parameter, events: [] }, release: { ...parameter, events: [] }, delayTime: { ...parameter, events: [] } };
    nodes.push(node); return node;
  };
  return {
    nodes, sampleRate: 8000,
    createGain: () => create('gain'), createBiquadFilter: () => create('biquad'), createDynamicsCompressor: () => create('compressor'),
    createWaveShaper: () => create('waveshaper'), createDelay: () => create('delay'), createConvolver: () => create('convolver'),
    createChannelSplitter: () => create('splitter'), createChannelMerger: () => create('merger'),
    createBuffer: (channels, length) => ({ channels, length, data: Array.from({ length: channels }, () => new Float32Array(length)), getChannelData(index) { return this.data[index]; } }),
  };
}

test('local preview distortion curves are bounded and change with drive and mode', () => {
  const soft = distortionCurve(18, 'soft', 101), hard = distortionCurve(18, 'hard', 101);
  assert.equal(soft.length, 101);
  assert.ok(Math.abs(soft[0] + (10 ** (18 / 20)) / (1 + 10 ** (18 / 20))) < 1e-6);
  assert.equal(hard[0], -1);
  assert.equal(hard[100], 1);
});

test('duck preview schedules each hit with a bounded release', () => {
  assert.deepEqual(duckAutomationEvents('x...x...........', 6, 150, 1, 120), [
    { start: 0, end: .15, floor: 10 ** (-6 / 20) },
    { start: .5, end: .65, floor: 10 ** (-6 / 20) },
  ]);
});

test('preview graph maps all seven effects to Web Audio nodes and keeps bypass dry', () => {
  const expected = { eq: 'biquad', filter: 'biquad', compressor: 'compressor', duck: 'gain', distortion: 'waveshaper', delay: 'delay', reverb: 'delay' };
  for (const [type, nodeKind] of Object.entries(expected)) {
    const context = fakeContext(), source = context.createGain(), output = context.createGain();
    effectGraph(context, source, output, type, { enabled: 'on' }, 120, 2);
    assert.ok(context.nodes.some((node) => node.kind === nodeKind), `${type} should use ${nodeKind}`);
  }
  const bypass = fakeContext(), source = bypass.createGain(), output = bypass.createGain();
  effectGraph(bypass, source, output, 'compressor', { enabled: 'off' }, 120, 2);
  assert.deepEqual(source.connections, [output]);
});

test('reverb preview wires deterministic server tap delays with linked decay feedback', () => {
  const context = fakeContext(), source = context.createGain(), output = context.createGain();
  effectGraph(context, source, output, 'reverb', { enabled: 'on', size: 50, decay: 60, mix: 25 }, 120, 2);
  const delays = context.nodes.filter((node) => node.kind === 'delay');
  const feedback = context.nodes.filter((node) => node.kind === 'gain' && node.connections.some((target) => target.kind === 'delay'));
  assert.equal(delays.length, 24, 'eight combs and four all-passes on each side');
  assert.equal(delays[0].delayTime.value, Math.floor(1116 * 8000 / 44100) / 8000);
  assert.ok(delays.some((node) => node.delayTime.value === Math.floor((1116 + 23) * 8000 / 44100) / 8000), 'the right side is a little longer');
  assert.ok(feedback.some((node) => Math.abs(node.gain.value - (0.7 + 0.28 * 60 / 95)) < 1e-12), 'the comb feedback follows the decay');
  assert.ok(context.nodes.some((node) => node.kind === 'splitter'));
  assert.ok(context.nodes.some((node) => node.kind === 'merger'));
});

test('convolution panel keeps impulse loading and convolution local with server-matched controls', () => {
  const context = fakeContext(), impulse = { sampleRate: 8000, length: 4, numberOfChannels: 2, getChannelData: (channel) => Float32Array.from(channel ? [0, 1, 0, 0] : [1, 0, 0, 0]) };
  const localIr = convolutionImpulse(context, impulse, { length: 3, normalize: 'off' });
  assert.equal(localIr.length, 4);
  assert.deepEqual([...localIr.data[0]], [.5, .5, 0, 0]);
  const source = context.createGain(), output = context.createGain(), graph = liveEffectGraph(context, source, output, 'convolution', { enabled: 'on', length: 3, normalize: 'off', predelay: 120, level: 6, mix: 30 }, 120, impulse);
  const convolver = context.nodes.find((node) => node.kind === 'convolver');
  const predelay = context.nodes.find((node) => node.kind === 'delay');
  const level = context.nodes.find((node) => node.kind === 'gain' && node.gain.value > 1);
  assert.equal(convolver.normalize, false);
  assert.equal(convolver.buffer.length, 4);
  assert.equal(predelay.delayTime.value, .12);
  assert.ok(Math.abs(level.gain.value - 10 ** (6 / 20)) < 1e-10);
  graph.dispose();
});

test('local effect input reads the selected immutable take without calling the effect renderer', async () => {
  const original = globalThis.fetch;
  let requested = '';
  globalThis.fetch = async (url) => { requested = String(url); return new Response(new Blob(['fixture'])); };
  try {
    const blob = await fetchTakeAudio('fake-take-id');
    assert.equal(blob.size, 7);
    assert.equal(requested, '/api/takes/fake-take-id/download?format=wav');
    await assert.rejects(() => fetchTakeAudio(''), /Build and select the upstream input/);
  } finally { globalThis.fetch = original; }
});
