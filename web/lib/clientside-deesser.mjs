import { pause } from './yield-slice.mjs';
import { DeesserCore } from './dynamics-core.mjs';

/** Attenuates the high band with AMPLE's linked detector timings; float math is an approximation (the core, dynamics-core.mjs, is the one the live worklet runs). */
export async function deesserAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, frames = audio.channels[0]?.length || 0, core = new DeesserCore(rate, audio.channels.length);
  core.configure(params);
  const output = audio.channels.map(() => new Float32Array(frames));
  for (let start = 0; start < frames; start += 65536) {
    signal?.throwIfAborted();
    core.process(audio.channels, output, start, Math.min(frames, start + 65536));
    await pause();
  }
  return { sampleRate: rate, channels: output };
}
