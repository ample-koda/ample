import assert from 'node:assert/strict';
import test from 'node:test';
import { duckAutomationEvents, effectGraph } from '../../app/studio/preview/audio.js';

function audioContext() {
  const created = [];
  const makeNode = (type) => {
    const node = { kind: type, connections: [], connect(next) { this.connections.push(next); return next; } };
    for (const key of ['gain', 'frequency', 'Q', 'threshold', 'ratio', 'attack', 'release', 'delayTime']) node[key] = { value: 0, events: [], setValueAtTime(value, time) { this.events.push(['set', value, time]); }, linearRampToValueAtTime(value, time) { this.events.push(['ramp', value, time]); } };
    created.push(node);
    return node;
  };
  const context = {
    sampleRate: 1000,
    created,
    createGain: () => makeNode('gain'),
    createBiquadFilter: () => makeNode('biquad'),
    createDynamicsCompressor: () => makeNode('compressor'),
    createWaveShaper: () => makeNode('waveshaper'),
    createDelay: () => makeNode('delay'),
    createConvolver: () => makeNode('convolver'),
    createChannelSplitter: () => makeNode('splitter'),
    createChannelMerger: () => makeNode('merger'),
    createOscillator: () => { const node = makeNode('oscillator'); node.start = () => { node.started = true; }; node.stop = () => { node.started = false; }; return node; },
    createBuffer: (channels, length) => {
      const data = Array.from({ length: channels }, () => new Float32Array(length));
      const buffer = makeNode('buffer');
      buffer.getChannelData = (channel) => data[channel];
      return buffer;
    },
  };
  return context;
}

test('local effect graphs connect each supported effect to output with mapped values', () => {
  const expected = {
    eq: ['biquad', 'biquad', 'biquad'],
    filter: ['biquad'],
    compressor: ['compressor', 'gain'],
    duck: ['gain'],
    distortion: ['waveshaper', 'biquad', 'gain', 'gain', 'gain', 'gain'],
    delay: ['delay', 'delay', 'gain', 'gain', 'gain', 'gain'],
    chorus: ['delay', 'oscillator', 'gain', 'gain', 'gain', 'gain'],
  };
  for (const [type, nodes] of Object.entries(expected)) {
    const context = audioContext(), source = { connections: [], connect(next) { this.connections.push(next); return next; } }, output = {};
    effectGraph(context, source, output, type, { enabled: 'on', threshold: '-21', ratio: '5', cutoff: '1700', pattern: 'x...............' }, 120, 1);
    assert.deepEqual(context.created.map((node) => node.kind), nodes, type);
    assert.ok(source.connections.length > 0, `${type} connects its source`);
    const terminal = context.created.find((node) => node.connections.includes(output));
    assert.ok(terminal, `${type} reaches the output`);
    if (type === 'compressor') {
      assert.equal(context.created[0].threshold.value, -21);
      assert.equal(context.created[0].ratio.value, 5);
    }
    if (type === 'filter') assert.equal(context.created[0].frequency.value, 1700);
    if (type === 'duck') assert.ok(context.created[0].gain.events.length > 0);
    if (type === 'chorus') {
      assert.equal(context.created[0].delayTime.value, 0.02);   // the fixed 20 ms base delay, matching the server's ModDelay(x, rate, 20, ...)
      assert.ok(context.created[1].started, 'the chorus LFO oscillator is started');
    }
  }
});

test('reverb connects a stereo comb/allpass delay network instead of an impulse approximation', () => {
  const context = audioContext(), source = { connections: [], connect(next) { this.connections.push(next); return next; } }, output = {};
  effectGraph(context, source, output, 'reverb', { size: '50', decay: '60', mix: '25' }, 120, 1);
  const delays = context.created.filter((node) => node.kind === 'delay');
  assert.equal(delays.length, 24, 'eight combs and four all-passes on each side');
  const rate = context.sampleRate;
  assert.ok(delays.some((node) => node.delayTime.value === Math.max(1, Math.floor(1116 * rate / 44100)) / rate), 'the first comb of the left side');
  assert.ok(delays.some((node) => node.delayTime.value === Math.max(1, Math.floor((1116 + 23) * rate / 44100)) / rate), 'the right side is a little longer');
  assert.ok(context.created.some((node) => node.kind === 'gain' && Math.abs(node.gain.value - (0.7 + 0.28 * 60 / 95)) < 1e-12), 'the comb feedback follows the decay');
  assert.ok(context.created.some((node) => node.kind === 'merger' && node.connections.length));
  assert.ok(context.created.some((node) => node.connections.includes(output)));
  assert.ok(!context.created.some((node) => node.kind === 'convolver'));
});

test('bypass is a direct source-to-output path and duck event timing follows tempo', () => {
  const context = audioContext(), source = { connections: [], connect(next) { this.connections.push(next); return next; } }, output = {};
  effectGraph(context, source, output, 'compressor', { enabled: 'off' }, 120, 1);
  assert.deepEqual(source.connections, [output]);
  assert.equal(context.created.length, 0);
  assert.deepEqual(duckAutomationEvents('x...............', 12, 150, 1, 120), [{ start: 0, end: 0.15, floor: 10 ** (-12 / 20) }]);
});
