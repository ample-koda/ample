// The AudioWorklet of the live reverb: it runs the same core as the offline render (reverb-stream.mjs), a block of 128 samples at a time, so the room you hear while a knob
// moves is the room of the render. Messages { size, decay, mix } set the knobs. Without a connected input it goes on with silence, so the tail rings out. @ample:reverb
import { ReverbStream } from './reverb-stream.mjs';

class ReverbProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super();
    this.left = new ReverbStream(sampleRate, 0);
    this.right = new ReverbStream(sampleRate, 1);
    if (options?.processorOptions) { this.left.configure(options.processorOptions); this.right.configure(options.processorOptions); }   // the knobs at the moment it is made
    this.port.onmessage = ({ data }) => { this.left.configure(data); this.right.configure(data); };
  }

  process(inputs, outputs) {
    const input = inputs[0] || [], out = outputs[0], left = input[0], right = input[1] || input[0];
    for (let i = 0; i < out[0].length; i++) {
      out[0][i] = this.left.process(left ? left[i] : 0);
      if (out[1]) out[1][i] = this.right.process(right ? right[i] : 0);
    }
    return true;
  }
}
registerProcessor('ample-reverb', ReverbProcessor);
