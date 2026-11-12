// The distortion block as a streaming core, shared by the offline preview (clientside-distortion.mjs) and the live AudioWorklet (distortion-worklet.mjs),
// so the sound you hear while a knob moves is the sound of the render. It follows Effects.Distortion of the server: drive in stages of at most 12 dB,
// four shaped points on the line from the last sample to this one and their mean (version 2, less aliasing fizz), a one-pole tone low pass, the level,
// then dry*(1-mix) + wet*mix. Local-preview chunk 2.1b: the Web Audio WaveShaper graph could not do the four-point mean and its oversampler shifted the wet path.
import { cutoffAlpha } from './one-pole.mjs';

const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
const read = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%|Hz/gi, '').trim());
  return clamp(Number.isFinite(parsed) ? Math.trunc(parsed) : fallback, min, max);   // the server reads whole numbers
};

/** The shaped value of one driven sample: hard clip or the soft curve x / (1 + |x|). */
export const shape = (x, hard) => (hard ? clamp(x, -1, 1) : x / (1 + Math.abs(x)));

export class DistortionCore {
  constructor(sampleRate, channels = 2) {
    this.rate = sampleRate; this.previous = new Float64Array(channels); this.low = new Float64Array(channels);
    this.configure({});
  }

  configure(params) {
    const drive = read(params.drive, 12, 0, 36);
    let gain = 1;
    for (let left = drive; left > 0; left -= 12) gain *= 10 ** (Math.min(left, 12) / 20);
    this.gain = gain; this.hard = params.mode === 'hard';
    this.alpha = cutoffAlpha(read(params.tone, 8000, 500, 16000), this.rate);
    this.level = 10 ** (read(params.level, 0, -24, 12) / 20);
    this.mix = read(params.mix, 100, 0, 100) / 100;
    this.bypass = params.enabled === 'off';
  }

  /** Reads ins[channel][i] and writes outs[channel][i] for i in [from, to); channels are independent. */
  process(ins, outs, from, to) {
    for (let c = 0; c < ins.length; c++) {
      const x = ins[c], y = outs[c];
      if (this.bypass) { if (y !== x) for (let i = from; i < to; i++) y[i] = x[i]; continue; }
      let previous = this.previous[c], low = this.low[c];
      const { gain, hard, alpha, level, mix } = this;
      for (let i = from; i < to; i++) {
        const now = x[i];
        let shaped = 0;
        for (let k = 1; k <= 4; k++) shaped += shape((previous + (now - previous) * k / 4) * gain, hard);
        low += alpha * (shaped / 4 - low);
        y[i] = now * (1 - mix) + low * level * mix;
        previous = now;
      }
      this.previous[c] = previous; this.low[c] = low;
    }
  }
}
