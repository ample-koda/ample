// The server's filter with no resonance, a single pass and a low or high pass (Effects.cs Filter / FilterSweep): two equal one-pole stages ("the pair"), as a streaming core for the live
// AudioWorklet (filter-pair-worklet.mjs) and the node tests. A Web Audio biquad cannot be this filter: the one-poles have their zeros at z = 0, a biquad low pass at z = -1, so the
// biquad is a sample late at the top and 28 dB off the render (the pair was -27.9 dB against the server as a biquad, about -61 dB here). Same maths as clientside-filter.mjs.
// The cutoff wobble comes in as a second signal (the LFO wave of live-lfo.mjs filterWave, -1..1): cutoff = base * 2^(depth * (wave + 1) / 2), as in the render. @ample:filter-pair
import { cutoffAlpha } from './one-pole.mjs';

export class FilterPairCore {
  constructor(rate, channels = 2) {
    this.rate = rate;
    this.state = Array.from({ length: channels }, () => ({ first: 0, second: 0, inFirst: 0, inSecond: 0 }));
    this.cutoff = 1000; this.highpass = false; this.depth = 0;
    this.lastKey = -1; this.a = 0; this.g = 0;
  }

  /** params { cutoff (Hz, 20..20000), highpass, depth (octaves of the wobble, 0 = none) }. */
  configure({ cutoff = 1000, highpass = false, depth = 0 } = {}) {
    this.cutoff = Math.max(20, Math.min(20000, Number(cutoff) || 1000)); this.highpass = Boolean(highpass); this.depth = Math.max(0, Number(depth) || 0); this.lastKey = -1;
  }

  coefficient(cutoff) {
    const key = this.highpass ? Math.max(1, Math.trunc(cutoff * 644 / 1000)) : Math.min(this.rate / 2 - 1, Math.trunc(cutoff * 1554 / 1000));
    if (key !== this.lastKey) {
      this.lastKey = key;
      if (this.highpass) { this.a = 1 - cutoffAlpha(key, this.rate); this.g = (1 + this.a) / 2; }   // a is b of the server (1 - alpha), g = (1 + b) / 2
      else this.a = cutoffAlpha(key, this.rate);
    }
  }

  /** Filters ins[c][start..end) into outs[c]; `lfo` is the wave (or null when there is no wobble). */
  process(ins, outs, lfo, start, end) {
    const wobble = this.depth > 0 && lfo;
    for (let i = start; i < end; i++) {
      this.coefficient(wobble ? Math.max(20, Math.min(20000, this.cutoff * 2 ** (this.depth * ((lfo[i] + 1) / 2)))) : this.cutoff);
      const a = this.a, g = this.g;
      for (let c = 0; c < outs.length; c++) {
        const s = this.state[c], x = ins[Math.min(c, ins.length - 1)][i];
        if (this.highpass) {
          const stage = g * (x - s.inFirst) + a * s.first; s.inFirst = x; s.first = stage;
          const out = g * (stage - s.inSecond) + a * s.second; s.inSecond = stage; s.second = out; outs[c][i] = out;
        } else {
          s.first += a * (x - s.first); s.second += a * (s.first - s.second); outs[c][i] = s.second;
        }
      }
    }
  }
}
