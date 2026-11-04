import { pause } from './yield-slice.mjs';
import { barBeats } from './meter.mjs';
import { cutoffAlpha } from './one-pole.mjs';
const number = (value, fallback, min, max) => {
  const text = String(value ?? '').trim();
  const parsed = Number.parseFloat(text.replace(/kHz/i, '')) * (/kHz/i.test(text) ? 1000 : 1);
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};
const cyclesPerBar = { '1/1': 1, '1/2': 2, '1/4': 4, '1/8': 8, '1/16': 16, '1/32': 32, '1/4t': 6, '1/8t': 12, '1/16t': 24 };

/** Applies AMPLE's two-stage low/high-pass filter and bar-aligned cutoff LFO with floating-point state. */
export async function filterAudio(audio, params, tempo, firstBar, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, base = number(params.cutoff, 1000, 20, 20000), depth = number(params.lfo_depth, 0, 0, 8);
  const cycles = cyclesPerBar[params.lfo_rate] || 0, barsamples = Math.floor((barBeats() * 60) * rate / (Number(tempo) || 120)), origin = (Math.max(1, firstBar) - 1) * barsamples;
  const shape = params.lfo_shape || 'sine';
  if (!['sine', 'triangle', 'saw', 'square'].includes(shape)) throw new Error(`Unsupported filter LFO shape “${shape}”.`);
  const type = params.type || 'lowpass', highpass = type === 'highpass', kind = ['bandpass', 'notch'].includes(type) ? type : '', passes = { 24: 2, 36: 3, 48: 4 }[String(params.slope ?? '12').trim()] || 1, channels = [], resonance = number(String(params.resonance ?? '').replace('%', ''), 0, 0, 100), q = 10 ** (resonance * 0.18 / 20), k = 1 / q;
  for (const source of audio.channels) {
    let input = source;
    for (let pass = 0; pass < passes; pass++) {
    const src = input, output = new Float32Array(source.length); let first = 0, second = 0, inFirst = 0, inSecond = 0, ic1 = 0, ic2 = 0;
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) {
        let cutoff = base;
        if (cycles > 0 && depth > 0) {
          const phase = (((origin + i) / barsamples * cycles) % 1 + 1) % 1;
          const wave = shape === 'triangle' ? phase < 0.5 ? phase * 2 : 2 - phase * 2
            : shape === 'saw' ? phase : shape === 'square' ? phase < 0.5 ? 0 : 1 : 0.5 - 0.5 * Math.cos(2 * Math.PI * phase);
          cutoff = Math.max(20, Math.min(20000, base * 2 ** (depth * wave)));
        }
        if (resonance > 0 || kind || passes > 1) {
          // two-pole state-variable filter, the same as the server (resonance 0 keeps the two one-pole stages)
          const g = Math.tan(Math.PI * Math.min(cutoff, rate * 0.45) / rate), a1 = 1 / (1 + g * (g + k)), a2 = g * a1, a3 = g * a2;
          const v3 = src[i] - ic2, v1 = a1 * ic1 + a2 * v3, v2 = ic2 + a2 * ic1 + a3 * v3;
          ic1 = 2 * v1 - ic1; ic2 = 2 * v2 - ic2;
          output[i] = kind === 'bandpass' ? k * v1 : kind === 'notch' ? src[i] - k * v1 : highpass ? src[i] - k * v1 - v2 : v2;
          continue;
        }
        // as on the server (filter version 4): the pair is -3 dB at the cutoff; a low pass is two stages at 1.554 times it, a high pass two one-pole high passes at 0.644 times
        if (highpass) {
          // two one-pole high passes, y = (1 + b) / 2 * (x - x1) + b * y1 with b = 1 - a: exactly 1 at the top, -3 dB together at the cutoff (as the server)
          const b = 1 - cutoffAlpha(Math.max(1, Math.trunc(cutoff * 644 / 1000)), rate), g = (1 + b) / 2;
          const stage = g * (src[i] - inFirst) + b * first; inFirst = src[i]; first = stage;
          const out = g * (stage - inSecond) + b * second; inSecond = stage; second = out;
          output[i] = out;
        } else {
          const coefficient = cutoffAlpha(Math.min(rate / 2 - 1, Math.trunc(cutoff * 1554 / 1000)), rate);
          first += coefficient * (src[i] - first);
          second += coefficient * (first - second);
          output[i] = second;
        }
      }
      await pause();
    }
    input = output;
    }
    channels.push(input);
  }
  return { sampleRate: rate, channels };
}
