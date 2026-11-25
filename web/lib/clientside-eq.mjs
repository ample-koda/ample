import { pause } from './yield-slice.mjs';
import { cutoffAlpha } from './one-pole.mjs';
import { serverInt, serverRoundedDb } from './server-int.mjs';
/** Splits the signal into AMPLE's two low-pass-derived bands and applies float dB gains. */
export async function eqAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate;
  // the bands are whole decibels on the server, a decimal part rounded (eq 3, Ample.Language.WholeDecibels)
  const band = (value) => 10 ** (Math.max(-12, Math.min(12, serverRoundedDb(value, 0))) / 20);
  const lowGain = band(params.low), midGain = band(params.mid), highGain = band(params.high);
  // corners written the wrong way round are put in order, as the server does (eq 3)
  const corner = (text, fallback, min, max) => Math.max(min, Math.min(max, serverInt(text, fallback)));   // whole hertz, as the server reads them
  const [lowHz, highHz] = [corner(params.low_hz, 250, 40, 2000), corner(params.high_hz, 3000, 1000, 12000)].sort((x, y) => x - y);
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
