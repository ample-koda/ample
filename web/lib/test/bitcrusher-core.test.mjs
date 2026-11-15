import test from 'node:test';
import assert from 'node:assert/strict';
import { BitcrusherCore } from '../bitcrusher-core.mjs';
import { bitcrusherAudio } from '../clientside-bitcrusher.mjs';

const wave = (n) => Float32Array.from({ length: n }, (_, i) => Math.sin(i * 0.37) * 0.9 + Math.sin(i * 1.9) * 0.05);

test('the live core in blocks of 128 gives exactly what the offline render gives, hold across the block edges included', async () => {
  const params = { bits: '5', downsample: '7', mix: '80%' };
  const source = wave(1000);
  const offline = (await bitcrusherAudio({ sampleRate: 48000, channels: [source] }, params, undefined)).channels[0];
  const core = new BitcrusherCore(1);
  core.configure(params);
  const live = new Float32Array(source.length);
  for (let start = 0; start < source.length; start += 128) core.process([source], [live], start, Math.min(source.length, start + 128));
  assert.deepEqual([...live], [...offline]);
});

test('every channel keeps its own held value', () => {
  const core = new BitcrusherCore(2);
  core.configure({ bits: '3', downsample: '4', mix: '100' });
  const left = Float32Array.of(0.9, 0.9, 0.9, 0.9), right = Float32Array.of(-0.5, -0.5, -0.5, -0.5);
  const outLeft = new Float32Array(4), outRight = new Float32Array(4);
  core.process([left, right], [outLeft, outRight], 0, 4);
  assert.ok(outLeft.every((v) => v === outLeft[0]) && outLeft[0] > 0);
  assert.ok(outRight.every((v) => v === outRight[0]) && outRight[0] < 0);
});

test('a dry mix and a bypass pass the sound on unchanged, and a knob that moves keeps the counter', () => {
  const core = new BitcrusherCore(1);
  const input = Float32Array.of(0.3, -0.2, 0.8, 0.1), out = new Float32Array(4);
  core.configure({ mix: '0' });
  core.process([input], [out], 0, 4);
  assert.deepEqual([...out], [...input]);
  core.configure({ enabled: 'off', mix: '100' });
  core.process([input], [out], 0, 4);
  assert.deepEqual([...out], [...input]);
  assert.equal(core.index, 8);   // the counter runs on, also while it passes
});

test('the planner treats a bitcrusher between a source and the master as a live effect with no built take of its own', async () => {
  const { planChain } = await import('../live-chain-model.mjs');
  const nodes = [{ id: 'click', tpl: 'click', params: {} }, { id: 'crush', tpl: 'bitcrusher', params: {} }, { id: 'master', tpl: 'master', params: {} }];
  const links = [{ from: 'click', to: 'crush', type: 'audio', inName: 'in' }, { from: 'crush', to: 'master', type: 'audio', inName: 'mix' }];
  const plan = planChain({ nodes, links, target: 'master', takeOf: () => null });
  assert.equal(plan.ok, true);
  assert.deepEqual(plan.items.map((item) => `${item.kind}:${item.tpl}`), ['local:click', 'effect:bitcrusher', 'effect:master']);
});
