// Streaming cores of the live flanger, phaser, EQ and parametric EQ (AudioWorklet fx-worklet.mjs, node tests): the maths of the offline renders (clientside-mod-delay.mjs,
// clientside-phaser.mjs, clientside-eq.mjs, clientside-peq.mjs), which the vectors of the server engine put at -60 dB or better, as a block-at-a-time process() instead of whole takes.
// A Web Audio graph cannot be these effects: a feedback loop of a delay node is never shorter than 128 samples (the flanger's is about 3 ms = 132 samples with the sweep below it),
// a biquad all-pass is not the server's first-order stages and a Web Audio shelf is not the server's one-pole bands. The sweep of the flanger and phaser comes in as a second signal
// (the LFO wave of live-lfo.mjs, -1..1, so the sweep has the phase it has in the render); the dry/wet mix stays in the graph (these cores give the wet sound only). @ample:fx
import { cutoffAlpha } from './one-pole.mjs';
import { peqBandList } from './clientside-peq.mjs';

const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/ms|%/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};
const dbNumber = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%|Hz/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};
const clamp = (value, min, max) => Math.max(min, Math.min(max, value));

/** The flanger: 3 ms base, depth 1..3 ms, delay in 1/256 sample, never below 2 samples, feedback up to 90 %; the output is the delayed sound (the graph mixes it). */
export class FlangerCore {
  constructor(rate, channels = 2) {
    this.rate = rate; this.size = 4096; this.mask = this.size - 1;
    this.rings = Array.from({ length: channels }, () => new Float32Array(this.size));
    this.count = 0; this.base = Math.trunc(3 * rate / 1000); this.depth = 0; this.feedback = 0;
    this.configure({});
  }

  configure(params = {}) {
    this.depth = Math.trunc(clamp(number(params.depth, 2), 1, 3) * this.rate / 1000);
    this.feedback = clamp(number(params.feedback, 50), 0, 90) / 100;
  }

  process(ins, outs, lfo, start, end) {
    for (let i = start; i < end; i++, this.count++) {
      const t = lfo ? Math.round(lfo[i] * 16384) + 16384 : 16384;   // the integer triangle 0..32767 of the server
      const delayQ8 = Math.max(512, this.base * 256 + Math.floor((t - 16384) * this.depth * 256 / 16384)), positionQ8 = this.count * 256 - delayQ8;
      for (let c = 0; c < outs.length; c++) {
        const ring = this.rings[c], x = ins[Math.min(c, ins.length - 1)][i];
        let delayed = 0;
        if (positionQ8 >= 0) {
          const index = Math.floor(positionQ8 / 256), fraction = (positionQ8 - index * 256) / 256;
          delayed = ring[index & this.mask] * (1 - fraction) + ring[Math.min(index + 1, this.count) & this.mask] * fraction;
        }
        ring[this.count & this.mask] = x + delayed * this.feedback;
        outs[c][i] = delayed;
      }
    }
  }
}

/** The phaser: 2..8 first-order all-pass stages whose coefficient follows the triangle; the output is the sound after the stages (the graph mixes it). */
export class PhaserCore {
  constructor(rate, channels = 2) {
    this.stages = 4;
    this.previousIn = Array.from({ length: channels }, () => new Float64Array(8));
    this.previousOut = Array.from({ length: channels }, () => new Float64Array(8));
    this.configure({});
  }

  configure(params = {}) { this.stages = Math.max(2, Math.min(8, Math.trunc(number(params.stages, 4)))); }

  process(ins, outs, lfo, start, end) {
    for (let i = start; i < end; i++) {
      const t = lfo ? Math.round((lfo[i] + 1) / 2 * 32767) : 0;   // phaserWave: t / 32767 * 2 - 1
      const coefficient = (3277 + Math.trunc(t * 26000 / 32768)) / 32768;
      for (let c = 0; c < outs.length; c++) {
        const pin = this.previousIn[c], pout = this.previousOut[c];
        let value = ins[Math.min(c, ins.length - 1)][i];
        for (let stage = 0; stage < this.stages; stage++) {
          const filtered = -coefficient * value + pin[stage] + coefficient * pout[stage];
          pin[stage] = value; pout[stage] = filtered; value = filtered;
        }
        outs[c][i] = value;
      }
    }
  }
}

/** The three-band EQ of the server (eq version 2): two one-pole low passes split the sound into low, mid and high, each with its own gain. */
export class EqCore {
  constructor(rate, channels = 2) {
    this.rate = rate; this.low = new Float64Array(channels); this.highEdge = new Float64Array(channels);
    this.configure({});
  }

  configure(params = {}) {
    this.lowGain = 10 ** (dbNumber(params.low, 0, -12, 12) / 20); this.midGain = 10 ** (dbNumber(params.mid, 0, -12, 12) / 20); this.highGain = 10 ** (dbNumber(params.high, 0, -12, 12) / 20);
    this.lowAlpha = cutoffAlpha(dbNumber(params.low_hz, 250, 40, 2000), this.rate); this.highAlpha = cutoffAlpha(dbNumber(params.high_hz, 3000, 1000, 12000), this.rate);
  }

  process(ins, outs, lfo, start, end) {
    for (let c = 0; c < outs.length; c++) {
      const source = ins[Math.min(c, ins.length - 1)], out = outs[c];
      let low = this.low[c], highEdge = this.highEdge[c];
      for (let i = start; i < end; i++) {
        const x = source[i];
        low += this.lowAlpha * (x - low); highEdge += this.highAlpha * (x - highEdge);
        out[i] = low * this.lowGain + (highEdge - low) * this.midGain + (x - highEdge) * this.highGain;
      }
      this.low[c] = low; this.highEdge[c] = highEdge;
    }
  }
}

/** The six-band parametric EQ: a cascade of normalised biquads (the bands that do something), the state of a band survives a knob turn. */
export class PeqCore {
  constructor(rate, channels = 2) {
    this.rate = rate; this.bands = [];
    this.state = Array.from({ length: channels }, () => Array.from({ length: 6 }, () => new Float64Array(4)));   // x1 x2 y1 y2 per band
    this.configure({});
  }

  configure(params = {}) { this.bands = peqBandList(params, this.rate); }

  process(ins, outs, lfo, start, end) {
    for (let c = 0; c < outs.length; c++) {
      const source = ins[Math.min(c, ins.length - 1)], out = outs[c], states = this.state[c];
      for (let i = start; i < end; i++) {
        let value = source[i];
        for (let band = 0; band < 6; band++) {
          const coefficients = this.bands[band];
          if (!coefficients) continue;
          const [b0, b1, b2, a1, a2] = coefficients, s = states[band], o = b0 * value + b1 * s[0] + b2 * s[1] - a1 * s[2] - a2 * s[3];
          s[1] = s[0]; s[0] = value; s[3] = s[2]; s[2] = o; value = Math.fround(o);   // the state stays in doubles, the sound between two bands is a float, as offline
        }
        out[i] = value;
      }
    }
  }
}

/** One core by block type. */
export function fxCore(kind, rate, channels = 2) {
  const Core = { flanger: FlangerCore, phaser: PhaserCore, eq: EqCore, peq: PeqCore }[kind];
  if (!Core) throw new Error(`no streaming core for ${kind}`);
  return new Core(rate, channels);
}
