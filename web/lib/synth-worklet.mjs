// The AudioWorklet of the live synth block (plan local-preview 3.3): notes arrive as messages { type: 'on', at, pitch, velocity, duration, pan, fine } with `at` on the clock of the audio
// context and are played by the SynthVoice of synth-core.mjs, the class the build preview runs, so a note on the piano roll sounds like the render of it. { type: 'settings', params }
// sets the block's knobs for the notes that start after it. At most 16 notes sound at once: the oldest is stolen. @ample:synth
import { SynthVoice, synthSettings } from './synth-core.mjs';

const MAX_VOICES = 16;

class SynthProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.settings = synthSettings({}, sampleRate); this.voices = [];
    this.port.onmessage = ({ data }) => {
      if (data.type === 'settings') this.settings = synthSettings(data.params || {}, sampleRate);
      else if (data.type === 'on') {
        if (!this.settings.enabled) return;
        const start = Math.max(currentFrame, Math.round(data.at * sampleRate));
        this.voices.push({ start, voice: new SynthVoice(this.settings, { pitch: data.pitch, velocity: data.velocity, held: Math.max(1, Math.floor(data.duration * sampleRate)), pan: data.pan, fine: data.fine }) });
        while (this.voices.length > MAX_VOICES) this.voices.shift();   // oldest first: they were pushed in order of arrival
      } else if (data.type === 'all-off') this.voices = [];
    };
  }

  process(_inputs, outputs) {
    const out = outputs[0], left = out[0], right = out[1] || out[0], n = left.length, end = currentFrame + n;
    for (const slot of this.voices) {
      if (slot.start >= end) continue;
      slot.voice.process(left, right, Math.max(0, slot.start - currentFrame), n);
    }
    this.voices = this.voices.filter((slot) => !slot.voice.done);
    return true;
  }
}
registerProcessor('ample-synth', SynthProcessor);
