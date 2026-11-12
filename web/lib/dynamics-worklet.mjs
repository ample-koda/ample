// The AudioWorklet of the live gate, limiter, de-esser and compressor (the compressor has a second input, the sidechain key): it runs the same cores as the offline renders (dynamics-core.mjs) a block of 128 samples at a time.
// processorOptions { kind, params } make the core; messages { params } turn the knobs. A limiter delays the sound by its look-ahead (it reports it as `latency`). @ample:dynamics
import { dynamicsCore } from './dynamics-core.mjs';

class DynamicsProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { kind, params } = options.processorOptions || {};
    this.core = dynamicsCore(kind, sampleRate, 2);
    this.core.configure(params || {});
    this.useKey = false;   // a message { key: true } says that a sound is connected to the key input (a silent unconnected input cannot be told from a connected silent one)
    this.port.onmessage = ({ data }) => { if ('key' in data) this.useKey = Boolean(data.key); if (data.params) this.core.configure(data.params); };
  }

  process(inputs, outputs) {
    const out = outputs[0], input = inputs[0], keys = this.useKey && inputs[1]?.length ? inputs[1] : null;
    const ins = input.length ? input : [new Float32Array(out[0].length)];   // silence while nothing is connected, so tails and look-ahead run out
    const channels = Math.min(out.length, ins.length === 1 ? 1 : 2);
    if (ins.length === 1 && out.length > 1) { this.core.process([ins[0]], [out[0]], 0, out[0].length, keys); out[1].set(out[0]); return true; }
    this.core.process(ins.slice(0, channels), out.slice(0, channels), 0, out[0].length, keys);
    return true;
  }
}
registerProcessor('ample-dynamics', DynamicsProcessor);
