import { pause } from './yield-slice.mjs';
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|LUFS|dB/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** Applies AMPLE peak normalization locally; LUFS mode remains on the server's BS.1770 path. */
export async function normalizeAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  if ((params.mode || 'peak') !== 'peak') throw new Error('Loudness normalization requires the server BS.1770 analysis path.');
  let maxPeak = 0;
  for (const channel of audio.channels) for (let start = 0; start < channel.length; start += 65536) {
    signal?.throwIfAborted();
    for (let i = start; i < Math.min(channel.length, start + 65536); i++) maxPeak = Math.max(maxPeak, Math.abs(channel[i]));
    await pause();
  }
  if (maxPeak === 0) return audio;
  const peakDb = 20 * Math.log10(maxPeak), gainDb = Math.max(-60, Math.min(number(params.max_gain, 48), number(params.peak, -1) - peakDb)), gain = 10 ** (gainDb / 20), channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length);
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) output[i] = Math.max(-1, Math.min(1, source[i] * gain));
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}
