import { pause } from './yield-slice.mjs';
import { DistortionCore } from './distortion-core.mjs';

/** Applies AMPLE's hard/soft transfer, tone low-pass and wet/dry blend with floating-point buffers (the core is shared with the live worklet). */
export async function distortionAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const channels = [];
  for (const source of audio.channels) {
    const output = new Float32Array(source.length), core = new DistortionCore(audio.sampleRate, 1);
    core.configure(params);
    for (let start = 0; start < source.length; start += 65536) {
      signal?.throwIfAborted();
      core.process([source], [output], start, Math.min(source.length, start + 65536));
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}
