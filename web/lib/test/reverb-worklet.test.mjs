// The live reverb worklet: a room that was bypassed (suspended) must wake up empty, not play out the old sound (the stub stands in for the AudioWorklet globals).
import test from 'node:test';
import assert from 'node:assert/strict';

let Processor;
globalThis.AudioWorkletProcessor = class { constructor() { this.port = {}; } };
globalThis.registerProcessor = (name, cls) => { if (name === 'ample-reverb') Processor = cls; };
globalThis.sampleRate = 44100;
await import('../reverb-worklet.mjs');

const block = (input) => ({ inputs: [[input, input]], outputs: [[new Float32Array(128), new Float32Array(128)]] });
const rms = (data) => Math.sqrt(data.reduce((sum, value) => sum + value * value, 0) / data.length);
const run = (node, input) => { const { inputs, outputs } = block(input); node.process(inputs, outputs); return rms(outputs[0][0]); };
const tone = () => Float32Array.from({ length: 128 }, (_, i) => 0.5 * Math.sin(i / 3));
const message = (node, data) => node.port.onmessage({ data });

test('a woken reverb is silent, like one that never played', () => {
  const played = new Processor({ processorOptions: { size: 100, decay: 95, mix: 100 } }), fresh = new Processor({ processorOptions: { size: 100, decay: 95, mix: 100 } });
  for (let i = 0; i < 200; i++) run(played, tone());   // fills the rings
  message(played, { suspended: true });
  run(played, new Float32Array(128));   // asleep: the block is skipped
  message(played, { suspended: false });
  const silent = new Float32Array(128);
  assert.equal(run(played, silent), run(fresh, silent));
  assert.equal(run(played, silent), 0);
});

test('a reverb that stays awake still rings out', () => {
  const node = new Processor({ processorOptions: { size: 100, decay: 95, mix: 100 } });
  for (let i = 0; i < 200; i++) run(node, tone());
  assert.ok(run(node, new Float32Array(128)) > 0.01);
});
