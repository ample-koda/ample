// The AudioWorklet of the live bitcrusher: the same core as the offline render (bitcrusher-core.mjs), a block of 128 samples at a time. @ample:bitcrusher
import { BitcrusherCore } from './bitcrusher-core.mjs';

class BitcrusherProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.core = new BitcrusherCore(2);
    this.core.configure(options.processorOptions?.params || {});
    this.port.onmessage = ({ data }) => this.core.configure(data.params || {});
  }

  process(inputs, outputs) {
    const out = outputs[0], input = inputs[0];
    if (!input.length) { for (const channel of out) channel.fill(0); return true; }
    const n = out[0].length;
    if (input.length === 1 && out.length > 1) { this.core.process([input[0]], [out[0]], 0, n); out[1].set(out[0]); return true; }
    this.core.process(input.slice(0, out.length), out.slice(0, input.length), 0, n);
    return true;
  }
}
registerProcessor('ample-bitcrusher', BitcrusherProcessor);
