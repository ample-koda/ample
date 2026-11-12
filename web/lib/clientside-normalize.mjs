import { pause } from './yield-slice.mjs';
import { LoudnessMeter } from './lufs-core.mjs';
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|LUFS|dB/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/**
 * The gain in dB that normalize applies to a sound, like the server (ExtraNormalize.cs): to a peak, or to an integrated loudness (BS.1770, lufs-core.mjs, never past -0.1 dBFS),
 * limited to -60 dB and `max_gain`. Returns { gainDb, peakDb, lufs } (lufs is null in peak mode or when the sound is too short or silent; gainDb is 0 for silence).
 */
export async function normalizeGain(audio, params, signal) {
  let maxPeak = 0;
  for (const channel of audio.channels) for (let start = 0; start < channel.length; start += 65536) {
    signal?.throwIfAborted();
    for (let i = start; i < Math.min(channel.length, start + 65536); i++) maxPeak = Math.max(maxPeak, Math.abs(channel[i]));
    await pause();
  }
  if (maxPeak === 0) return { gainDb: 0, peakDb: -Infinity, lufs: null };
  const peakDb = 20 * Math.log10(maxPeak);
  let gainDb, lufs = null;
  if ((params.mode || 'peak') === 'loudness') {
    const channels = audio.channels.slice(0, 2), meter = new LoudnessMeter(audio.sampleRate, channels.length);
    // the server needs one block (400 ms) of sound to measure; a shorter sound is left at its level
    if (channels[0].length >= Math.floor(audio.sampleRate * 400 / 1000)) {
      for (let start = 0; start < channels[0].length; start += 65536) { signal?.throwIfAborted(); meter.process(channels, start, Math.min(channels[0].length, start + 65536)); await pause(); }
      const value = meter.integrated();
      lufs = Number.isFinite(value) ? value : null;
    }
    gainDb = lufs === null ? 0 : number(params.loudness, -18) - lufs;
    gainDb = Math.min(gainDb, -0.1 - peakDb);
  } else gainDb = number(params.peak, -1) - peakDb;
  return { gainDb: Math.max(-60, Math.min(number(params.max_gain, 48), gainDb)), peakDb, lufs };
}

/** Applies AMPLE normalization locally: to a peak or to an integrated loudness (the same gain as the server's). */
export async function normalizeAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const { gainDb, peakDb } = await normalizeGain(audio, params, signal);
  if (peakDb === -Infinity) return audio;
  const gain = 10 ** (gainDb / 20), channels = [];
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
