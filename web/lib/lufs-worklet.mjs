// AudioWorklet that measures loudness (lufs-core.mjs) of what goes through it and posts { momentary, shortTerm, integrated } about ten times a second. It passes the sound on unchanged.
import { LoudnessMeter } from './lufs-core.mjs';

class LoudnessProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.meter = new LoudnessMeter(sampleRate, 2); this.since = 0;
    this.port.onmessage = ({ data }) => { if (data?.reset) this.meter = new LoudnessMeter(sampleRate, 2); };
  }

  process(inputs, outputs) {
    const input = inputs[0], output = outputs[0];
    if (!input.length) return true;
    for (let c = 0; c < output.length; c++) output[c].set(input[Math.min(c, input.length - 1)]);
    const frames = input[0].length;
    this.meter.process(input, 0, frames);
    this.since += frames;
    if (this.since >= sampleRate / 10) {
      this.since = 0;
      this.port.postMessage({ momentary: this.meter.momentary(), shortTerm: this.meter.shortTerm(), integrated: this.meter.integrated() });
    }
    return true;
  }
}
registerProcessor('ample-lufs', LoudnessProcessor);
