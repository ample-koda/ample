import { pause } from './yield-slice.mjs';
import { GateCore } from './dynamics-core.mjs';

/** AMPLE linked-channel gate with its peak envelope, hold counter and attack/release ramp (the core, dynamics-core.mjs, is the one the live worklet runs). */
export async function gateAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length, core = new GateCore(rate);
  core.configure(params);
  const output = audio.channels.map(() => new Float32Array(length));
  for (let start = 0; start < length; start += 0x10000) {
    signal?.throwIfAborted(); await pause();
    core.process(audio.channels, output, start, Math.min(length, start + 0x10000));
  }
  return { sampleRate: rate, channels: output };
}
