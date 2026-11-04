import { pause } from './yield-slice.mjs';
import { cutoffAlpha } from './one-pole.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%|Hz/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** Applies AMPLE's hard/soft transfer, tone low-pass and wet/dry blend with floating-point buffers. */
export async function distortionAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, drive = number(params.drive, 12, 0, 36), mode = params.mode || 'soft';
  let gain = 1;
  for (let left = drive; left > 0; left -= 12) gain *= 10 ** (Math.min(left, 12) / 20);
  const tone = number(params.tone, 8000, 500, 16000), coefficient = cutoffAlpha(tone, rate);
  const level = 10 ** (number(params.level, 0, -24, 12) / 20), mix = number(params.mix, 100, 0, 100) / 100;
  const channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length); let low = 0;
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) {
        // four shaped points on the line from the last sample to this one, and their mean (as on the server, distortion version 2): far less aliasing fizz
        const previous = i > 0 ? source[i - 1] : 0;
        let shaped = 0;
        for (let k = 1; k <= 4; k++) {
          const driven = (previous + (source[i] - previous) * k / 4) * gain;
          shaped += mode === 'hard' ? Math.max(-1, Math.min(1, driven)) : driven / (1 + Math.abs(driven));
        }
        shaped /= 4;
        low += coefficient * (shaped - low);
        output[i] = source[i] * (1 - mix) + low * level * mix;
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: rate, channels };
}
