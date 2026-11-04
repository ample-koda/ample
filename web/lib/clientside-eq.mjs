import { pause } from './yield-slice.mjs';
import { cutoffAlpha } from './one-pole.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%|Hz/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** Splits the signal into AMPLE's two low-pass-derived bands and applies float dB gains. */
export async function eqAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate;
  const lowGain = 10 ** (number(params.low, 0, -12, 12) / 20), midGain = 10 ** (number(params.mid, 0, -12, 12) / 20), highGain = 10 ** (number(params.high, 0, -12, 12) / 20);
  const lowHz = number(params.low_hz, 250, 40, 2000), highHz = number(params.high_hz, 3000, 1000, 12000);
  const coeff = (hz) => cutoffAlpha(hz, rate);   // the -3 dB point where it is asked, as on the server (eq version 2)
  const lowAlpha = coeff(lowHz), highAlpha = coeff(highHz), channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length); let low = 0, highEdge = 0;
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) {
        low += lowAlpha * (source[i] - low);
        highEdge += highAlpha * (source[i] - highEdge);
        output[i] = low * lowGain + (highEdge - low) * midGain + (source[i] - highEdge) * highGain;
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: rate, channels };
}
