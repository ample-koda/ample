import { pause } from './yield-slice.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|Hz/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** The normalised biquad of one band (RBJ cookbook), the twin of PeqRenderer.Coefficients in Ample.Core. */
export function peqCoefficients(type, hz, gainDb, q, rate) {
  const w0 = 2 * Math.PI * Math.max(20, Math.min(rate * 0.45, hz)) / rate, cw = Math.cos(w0), sw = Math.sin(w0), alpha = sw / (2 * Math.max(0.1, q)), a = 10 ** (gainDb / 40);
  let b0, b1, b2, a0, a1, a2;
  if (type === 'lowcut') { b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = b0; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; }
  else if (type === 'highcut') { b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = b0; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; }
  else if (type === 'notch') { b0 = 1; b1 = -2 * cw; b2 = 1; a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha; }
  else if (type === 'lowshelf' || type === 'highshelf') {
    const beta = 2 * Math.sqrt(a) * alpha;
    if (type === 'lowshelf') { b0 = a * ((a + 1) - (a - 1) * cw + beta); b1 = 2 * a * ((a - 1) - (a + 1) * cw); b2 = a * ((a + 1) - (a - 1) * cw - beta); a0 = (a + 1) + (a - 1) * cw + beta; a1 = -2 * ((a - 1) + (a + 1) * cw); a2 = (a + 1) + (a - 1) * cw - beta; }
    else { b0 = a * ((a + 1) + (a - 1) * cw + beta); b1 = -2 * a * ((a - 1) + (a + 1) * cw); b2 = a * ((a + 1) + (a - 1) * cw - beta); a0 = (a + 1) - (a - 1) * cw + beta; a1 = 2 * ((a - 1) - (a + 1) * cw); a2 = (a + 1) - (a - 1) * cw - beta; }
  } else { b0 = 1 + alpha * a; b1 = -2 * cw; b2 = 1 - alpha * a; a0 = 1 + alpha / a; a1 = -2 * cw; a2 = 1 - alpha / a; }
  return [b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0];
}

/** The bands that change the sound: a notch, lowcut or highcut always, any other type only with a gain other than 0 dB; a band switched off never. */
export function activePeqBands(params, rate) {
  const bands = [];
  for (let n = 1; n <= 6; n++) {
    if (String(params[`band${n}_enabled`] ?? 'on').trim() === 'off') continue;
    const type = params[`band${n}_type`] || (n === 1 ? 'lowshelf' : n === 6 ? 'highshelf' : 'peak'), gain = number(params[`band${n}_gain`], 0, -24, 24);
    if (!['notch', 'lowcut', 'highcut'].includes(type) && gain === 0) continue;
    const defaults = [80, 250, 800, 2500, 6000, 12000];
    bands.push(peqCoefficients(type, number(params[`band${n}_hz`], defaults[n - 1], 20, 20000), gain, number(params[`band${n}_q`], 1, 0.1, 18), rate));
  }
  return bands;
}

/** Applies the parametric EQ with floating-point state (the server rounds to 16 bit only at the end). */
export async function peqAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, bands = activePeqBands(params, rate);
  if (!bands.length) return audio;
  const channels = [];
  for (const source of audio.channels) {
    const y = Float32Array.from(source);
    for (const [b0, b1, b2, a1, a2] of bands) {
      let x1 = 0, x2 = 0, y1 = 0, y2 = 0;
      for (let start = 0; start < y.length; start += 65536) {
        signal?.throwIfAborted();
        const end = Math.min(y.length, start + 65536);
        for (let i = start; i < end; i++) {
          const input = y[i], o = b0 * input + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
          x2 = x1; x1 = input; y2 = y1; y1 = o; y[i] = o;
        }
        await pause();
      }
    }
    channels.push(y);
  }
  return { sampleRate: rate, channels };
}
