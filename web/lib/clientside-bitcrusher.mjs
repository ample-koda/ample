import { pause } from './yield-slice.mjs';
import { BitcrusherCore } from './bitcrusher-core.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** Applies AMPLE's bit-depth quantization and sample hold to decoded float buffers. */
export async function bitcrusherAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const probe = new BitcrusherCore(1);
  probe.configure(params);
  if (probe.passes) return audio;
  const channels = [];
  for (const source of audio.channels) {
    // the core of the live worklet, one channel at a time from the first sample: the same arithmetic as while a knob moves
    const core = new BitcrusherCore(1), output = new Float32Array(source.length);
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
