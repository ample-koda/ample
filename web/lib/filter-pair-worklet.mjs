// The AudioWorklet of the live filter in its plain form (low or high pass, 12 dB, no resonance): the same core as the offline render (filter-pair-core.mjs), 128 samples at a time.
// Input 0 is the sound, input 1 the LFO wave of the cutoff wobble (-1..1, unconnected or silent = no wobble). Messages { params } turn the knobs. @ample:filter-pair
import { FilterPairCore } from './filter-pair-core.mjs';

class FilterPairProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.core = new FilterPairCore(sampleRate, 2);
    this.core.configure(options.processorOptions?.params || {});
    this.port.onmessage = ({ data }) => { if (data.params) this.core.configure(data.params); };
  }

  process(inputs, outputs) {
    const out = outputs[0], input = inputs[0], lfo = inputs[1]?.[0] || null;
    if (!input.length) { for (const channel of out) channel.fill(0); return true; }
    this.core.process(input, out, lfo, 0, out[0].length);
    return true;
  }
}
registerProcessor('ample-filter-pair', FilterPairProcessor);
