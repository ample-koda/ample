// The AudioWorklet of the live felt piano (plan local-preview 3.4): messages { type: 'on', at, pitch, duration } start a note at a time on the clock of the audio context; the notes are
// played by the PianoVoice of piano-core.mjs, the class the build preview runs. Sixteen notes at most, the oldest is stolen. @ample:piano
import { PianoVoice } from './piano-core.mjs';

class PianoProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.voices = []; this.mono = new Float32Array(128);
    this.port.onmessage = ({ data }) => {
      if (data.type === 'on') {
        this.voices.push({ start: Math.max(currentFrame, Math.round(data.at * sampleRate)), voice: new PianoVoice(sampleRate, { pitch: data.pitch, length: Math.max(1, data.duration * sampleRate) }) });
        while (this.voices.length > 16) this.voices.shift();
      } else if (data.type === 'all-off') this.voices = [];
    };
  }

  process(_inputs, outputs) {
    const out = outputs[0], n = out[0].length, end = currentFrame + n;
    if (this.mono.length < n) this.mono = new Float32Array(n);
    this.mono.fill(0, 0, n);
    for (const slot of this.voices) if (slot.start < end) slot.voice.process(this.mono, Math.max(0, slot.start - currentFrame), n);
    for (let c = 0; c < out.length; c++) out[c].set(this.mono.subarray(0, n));
    this.voices = this.voices.filter((slot) => !slot.voice.done);
    return true;
  }
}
registerProcessor('ample-piano', PianoProcessor);
