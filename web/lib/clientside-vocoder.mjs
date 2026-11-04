// The vocoder in the browser: the same steps as Vocoder.Process of the engine (ExtraVocoder.cs): a voice (the modulator) and a carrier are split into the same band-pass bands, the loudness of every
// band of the voice sets that of the carrier, noise that follows the highest band keeps s and t clear, and the level is matched to the carrier. Float math: an approximation of the engine. @ample:vocoder
import { pause } from './yield-slice.mjs';

const number = (value, fallback, min, max) => { const parsed = Number.parseFloat(String(value ?? '').replace(/[a-z %]/gi, '')); return Number.isFinite(parsed) ? Math.min(max, Math.max(min, parsed)) : fallback; };

/** The settings of a vocoder block (the parameter texts of the song). */
export function vocoderSettings(params) {
  return {
    bands: Math.round(number(params.bands, 20, 4, 40)), low: number(params.low, 120, 40, 2000), high: number(params.high, 7500, 1000, 16000), attack: number(params.attack, 4, 0.1, 100),
    release: number(params.release, 70, 1, 1000), width: number(params.width, 140, 50, 300) / 100, sibilance: number(params.sibilance, 35, 0, 100) / 100, shift: number(params.shift, 0, -24, 24),
    mix: number(params.mix, 100, 0, 100) / 100, gain: number(params.gain, 0, -36, 12),
  };
}

const coefficients = (centre, q, rate) => { const w = 2 * Math.PI * Math.min(centre, rate * 0.45) / rate, alpha = Math.sin(w) / (2 * q), a0 = 1 + alpha; return { b0: alpha / a0, a1: -2 * Math.cos(w) / a0, a2: (1 - alpha) / a0 }; };

/** A bank of cascaded band-pass pairs: step(x, out) writes the output of every band into out. */
function bank(centres, q, rate) {
  const k = centres.map((c) => coefficients(c, q, rate)), n = centres.length;
  const state = new Float64Array(n * 8);   // per band: stage one x1 x2 y1 y2, stage two x1 x2 y1 y2 (two floats of padding are not needed: 8 slots)
  return (x, out) => {
    for (let b = 0; b < n; b++) {
      const { b0, a1, a2 } = k[b], o = b * 8;
      const y1 = b0 * x - b0 * state[o + 1] - a1 * state[o + 2] - a2 * state[o + 3];
      state[o + 1] = state[o]; state[o] = x; state[o + 3] = state[o + 2]; state[o + 2] = y1;
      const y2 = b0 * y1 - b0 * state[o + 5] - a1 * state[o + 6] - a2 * state[o + 7];
      state[o + 5] = state[o + 4]; state[o + 4] = y1; state[o + 7] = state[o + 6]; state[o + 6] = y2;
      out[b] = y2;
    }
  };
}

/**
 * Vocodes the carrier ({ sampleRate, channels }) with the voice (modulator, any number of channels, mixed to mono). Returns { sampleRate, channels } as long as the longer of the two.
 */
export async function vocodeAudio(modulator, carrier, params, signal) {
  const s = vocoderSettings(params), rate = carrier.sampleRate;
  if (params.enabled === 'off') return carrier;
  const voiceFrames = modulator.channels[0]?.length || 0, frames = Math.max(voiceFrames, ...carrier.channels.map((c) => c.length));
  const bands = s.bands, low = Math.min(s.low, rate * 0.2), high = Math.min(Math.max(s.high, low * 1.5), rate * 0.45), spacing = Math.log2(high / low) / Math.max(1, bands - 1);
  const octaves = Math.max(0.1, spacing * s.width), q = 1 / (2 ** (octaves / 2) - 2 ** (-octaves / 2));
  const centres = Array.from({ length: bands }, (_, b) => low * 2 ** (spacing * b)), factor = 2 ** (s.shift / 12);
  const attack = 1 - Math.exp(-1 / (rate * Math.max(0.1, s.attack) / 1000)), release = 1 - Math.exp(-1 / (rate * Math.max(1, s.release) / 1000));
  const voiceBank = bank(centres, q, rate), carrierBanks = carrier.channels.map(() => bank(centres.map((c) => c * factor), q, rate));
  const envelope = new Float64Array(bands), voiceBands = new Float64Array(bands), carrierBands = new Float64Array(bands);
  const result = carrier.channels.map(() => new Float64Array(frames)), energyOut = carrier.channels.map(() => 0), energyCarrier = carrier.channels.map(() => 0);
  const hp = Math.exp(-2 * Math.PI * 5000 / rate);
  let sibilant = 0, highPass = 0, noiseState = 0, rng = 1;
  const noise = () => { rng = (Math.imul(rng, 1103515245) + 12345) >>> 0; return (((rng >>> 16) & 0x7fff) / 16384) - 1; };
  for (let i = 0; i < frames; i++) {
    if ((i & 32767) === 0) { signal?.throwIfAborted(); await pause(); }
    let m = 0;
    if (i < voiceFrames) { for (const c of modulator.channels) m += c[i]; m /= modulator.channels.length; }
    voiceBank(m, voiceBands);
    for (let b = 0; b < bands; b++) { const level = Math.abs(voiceBands[b]); envelope[b] += (level > envelope[b] ? attack : release) * (level - envelope[b]); }
    highPass = hp * highPass + (1 - hp) * m;
    const hf = m - highPass;
    sibilant += (Math.abs(hf) > sibilant ? attack : release) * (Math.abs(hf) - sibilant);
    noiseState = hp * noiseState + (1 - hp) * noise();
    const hiss = (noise() - noiseState) * sibilant * 4 * s.sibilance;
    for (let c = 0; c < carrier.channels.length; c++) {
      const x = i < carrier.channels[c].length ? carrier.channels[c][i] : 0;
      carrierBanks[c](x, carrierBands);
      let sum = 0;
      for (let b = 0; b < bands; b++) sum += carrierBands[b] * envelope[b];
      sum += hiss;
      result[c][i] = sum; energyOut[c] += sum * sum; energyCarrier[c] += x * x;
    }
  }
  const gain = 10 ** (s.gain / 20);
  const channels = result.map((out, c) => {
    const scale = energyOut[c] > 1e-18 ? Math.sqrt(energyCarrier[c] / energyOut[c]) * gain : 0, dry = carrier.channels[c], made = new Float32Array(frames);
    for (let i = 0; i < frames; i++) made[i] = (i < dry.length ? dry[i] : 0) * (1 - s.mix) + out[i] * scale * s.mix;
    return made;
  });
  return { sampleRate: rate, channels };
}
