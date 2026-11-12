import { pause } from './yield-slice.mjs';
import { panGains } from './mix-laws.mjs';

/** Applies a scalar equal-power pan approximation, preserving stereo balance and spreading mono to stereo. */
export async function panAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const pan = Math.max(-32, Math.min(32, Number.parseInt(params.pan, 10) || 0));
  const { left: leftScale, right: rightScale } = panGains(pan, audio.channels.length > 1);   // the table of the server (PanTable)
  const channels = audio.channels.length > 1 ? audio.channels.slice(0, 2) : [audio.channels[0], audio.channels[0]], result = [];
  for (let channel = 0; channel < 2; channel++) {
    const source = channels[channel], output = new Float32Array(source.length), scale = channel ? rightScale : leftScale;
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(source.length, start + 65536);
      for (let i = start; i < end; i++) output[i] = source[i] * scale;
      await pause();
    }
    result.push(output);
  }
  return { sampleRate: audio.sampleRate, channels: result };
}
