import { pause } from './yield-slice.mjs';
import { CompressorCore } from './dynamics-core.mjs';

/** AMPLE-linked compressor. Float envelope/gain math approximates the server Q15 path. The maths is CompressorCore (dynamics-core.mjs), also run by the live AudioWorklet. */
export async function compressorAudio(audio, params, keyAudio, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length, channels = audio.channels;
  const core = new CompressorCore(rate);
  if (keyAudio?.sampleRate && keyAudio.sampleRate !== rate) core.setKeyRate(keyAudio.sampleRate);
  core.configure(params);
  const output = channels.map(() => new Float32Array(length));
  for (let from = 0; from < length; from += 0x10000) {
    signal?.throwIfAborted(); await pause();
    core.process(channels, output, from, Math.min(length, from + 0x10000), keyAudio?.channels || null);
  }
  return { sampleRate: rate, channels: output };
}
