// The AudioWorklet of the live Delay Llama: it hands blocks of 128 samples to the streaming voice (llama-stream.mjs) and passes on the messages
// { type: 'settings' | 'on' | 'off' | 'all-off' } (a time `at` is on the clock of the audio context). @ample:delay-llama
import { LlamaStream } from './llama-stream.mjs';

class DelayLlamaProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.core = new LlamaStream(sampleRate);
    this.port.onmessage = ({ data }) => {
      if (data.type === 'settings') this.core.settings(data.settings);
      else if (data.type === 'on') this.core.noteOn(data.pitch, data.velocity, data.at, data.fine);
      else if (data.type === 'off') this.core.noteOff(data.pitch, data.at);
      else if (data.type === 'all-off') this.core.allOff();
      else if (data.type === 'bend') this.core.bend(data.semitones);
    };
  }

  process(_inputs, outputs) {
    const out = outputs[0];
    this.core.render(out[0], out[1] || out[0], out[0].length, currentTime);
    return true;
  }
}
registerProcessor('delay-llama', DelayLlamaProcessor);
