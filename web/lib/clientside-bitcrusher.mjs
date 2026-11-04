import { pause } from './yield-slice.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** Applies AMPLE's bit-depth quantization and sample hold to decoded float buffers. */
export async function bitcrusherAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const bits = Math.round(number(params.bits, 8, 2, 16)), hold = Math.round(number(params.downsample, 1, 1, 64)), mix = number(params.mix, 100, 0, 100) / 100;
  if (mix === 0) return audio;
  const shift = 16 - bits, channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length); let held = 0;
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) {
        if (i % hold === 0) held = (Math.trunc(Math.max(-1, Math.min(1, source[i])) * 32768) >> shift) << shift;
        const dry = Math.trunc(Math.max(-1, Math.min(1, source[i])) * 32768);
        output[i] = Math.max(-1, Math.min(1, (dry * (1 - mix) + held * mix) / 32768));
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}
