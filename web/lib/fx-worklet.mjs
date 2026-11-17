// The AudioWorklet of the live flanger, phaser, EQ and parametric EQ: the cores of fx-core.mjs, 128 samples at a time.
// processorOptions { kind, params }; input 0 is the sound, input 1 the LFO wave of the sweep (flanger, phaser; -1..1). Messages { params } turn the knobs. @ample:fx
import { fxCore } from './fx-core.mjs';

class FxProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { kind, params } = options.processorOptions || {};
    this.core = fxCore(kind, sampleRate, 2);
    this.core.configure(params || {});
    this.port.onmessage = ({ data }) => { if (data.params) this.core.configure(data.params); };
  }

  process(inputs, outputs) {
    const out = outputs[0], input = inputs[0], lfo = inputs[1]?.[0] || null;
    const ins = input.length ? input : [new Float32Array(out[0].length)];   // silence while nothing is connected, so the tails run out
    this.core.process(ins, out, lfo, 0, out[0].length);
    return true;
  }
}
registerProcessor('ample-fx', FxProcessor);
