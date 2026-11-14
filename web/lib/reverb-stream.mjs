// The room of the server, one sample at a time: the core that both the offline render (clientside-reverb.mjs) and the live AudioWorklet (reverb-worklet.mjs) run, so the preview
// you hear while a knob moves is the same sound as the render, sample for sample (the pattern of the Delay Llama). The input loses its lows (160 Hz), eight damped combs ring in
// parallel and four all-passes smooth the tail; the dry sound stays and mix sets the level of the room. Integer arithmetic like the server (Effects.cs Reverb).
import { cutoffCoefficient } from './one-pole.mjs';

// The comb and all-pass lengths of the classic Freeverb (samples at 44.1 kHz); the right channel is a little longer.
const COMBS = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617], ALLPASSES = [556, 441, 341, 225], SPREAD = 23, DAMPING = 9830;
const MAX_SIZE_FACTOR = (60 + 100) / 110;

const clamp = (input, minimum, maximum) => Math.max(minimum, Math.min(maximum, input));
const value = (input, fallback) => { const parsed = Number.parseFloat(input); return Number.isFinite(parsed) ? parsed : fallback; };
const inputPcm = (input) => Math.round(clamp(input * (input < 0 ? 32768 : 32767), -32768, 32767));

/** One channel of the room. channel 1 (the right) has slightly longer lines. */
export class ReverbStream {
  constructor(sampleRate, channel = 0) {
    this.rate = sampleRate; this.spread = channel === 1 ? SPREAD : 0;
    this.low = cutoffCoefficient(160, sampleRate); this.state = 0;
    // the rings are allocated for the largest room, so size can change while it runs (the line is just read shorter or longer)
    this.combs = COMBS.map(() => ({ ring: null, length: 1, at: 0, store: 0 }));
    COMBS.forEach((base, k) => { this.combs[k].ring = new Float64Array(Math.ceil((base + this.spread) * sampleRate * MAX_SIZE_FACTOR / 44100) + 2); });
    this.allpasses = ALLPASSES.map((base) => { const length = Math.max(1, Math.trunc((base + this.spread) * sampleRate / 44100)); return { ring: new Float64Array(length), length, at: 0 }; });
    this.configure({});
  }

  /** Takes size (1..100), decay (0..95) and mix (0..100) from the block's parameters (text or numbers). */
  configure(params) {
    const size = Math.trunc(clamp(value(params.size, 50), 1, 100)), decay = Math.trunc(clamp(value(params.decay, 60), 0, 95));
    this.mix = Math.trunc(clamp(value(params.mix, 25), 0, 100));
    this.feedback = 22938 + Math.trunc(9175 * decay / 95);
    const g = this.feedback / 32768;
    this.gain = Math.round(32768 * Math.sqrt(0.5 * (1 - (g * g)) / COMBS.length));
    COMBS.forEach((base, k) => {
      const comb = this.combs[k], length = Math.max(1, Math.trunc((base + this.spread) * this.rate * (60 + size) / (44100 * 110)));
      if (length !== comb.length) { comb.length = length; if (comb.at >= length) comb.at = 0; }
    });
  }

  /**
   * Forgets the sound that was in the room (rings, damping and the low-cut), as a room that was never fed:
   * a bypassed live reverb calls it when it stops computing, so waking it up does not play old sound.
   */
  reset() {
    this.state = 0;
    for (const comb of this.combs) { comb.ring.fill(0); comb.at = 0; comb.store = 0; }
    for (const allpass of this.allpasses) { allpass.ring.fill(0); allpass.at = 0; }
  }

  /** One sample in (-1..1), one sample out. */
  process(input) {
    const pcm = inputPcm(input);
    this.state += Math.floor(this.low * (pcm - this.state) / 32768);
    const feed = Math.floor((pcm - this.state) * this.gain / 32768);
    let wet = 0;
    for (const comb of this.combs) {
      const delayed = comb.ring[comb.at];
      comb.store = Math.floor(((delayed * (32768 - DAMPING)) + (comb.store * DAMPING)) / 32768);
      comb.ring[comb.at] = feed + Math.floor(comb.store * this.feedback / 32768);
      wet += delayed;
      comb.at = comb.at + 1 === comb.length ? 0 : comb.at + 1;
    }
    for (const allpass of this.allpasses) {
      const delayed = allpass.ring[allpass.at], current = wet;
      wet = delayed - current;
      allpass.ring[allpass.at] = current + Math.floor(delayed / 2);
      allpass.at = allpass.at + 1 === allpass.length ? 0 : allpass.at + 1;
    }
    const sample = clamp(pcm + Math.trunc(wet * this.mix / 100), -32768, 32767);
    return sample < 0 ? sample / 32768 : sample / 32767;
  }
}
