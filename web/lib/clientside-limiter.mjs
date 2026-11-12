import { pause } from './yield-slice.mjs';
import { LimiterCore } from './dynamics-core.mjs';

/** AMPLE linked-channel lookahead limiter; float math approximates its Q15 gain path (the core, dynamics-core.mjs, is the one the live worklet runs). */
export async function limiterAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length, core = new LimiterCore(rate, audio.channels.length);
  core.configure(params);
  // the core delays the sound by its look-ahead: run it over the take followed by silence and keep what comes out after that delay
  const total = length + core.latency, padded = audio.channels.map((channel) => { const copy = new Float32Array(total); copy.set(channel); return copy; }), out = audio.channels.map(() => new Float32Array(total));
  for (let start = 0; start < total; start += 0x10000) {
    signal?.throwIfAborted(); await pause();
    core.process(padded, out, start, Math.min(total, start + 0x10000));
  }
  return { sampleRate: rate, channels: out.map((channel) => channel.slice(core.latency)) };
}
