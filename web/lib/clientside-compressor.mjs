import { pause } from './yield-slice.mjs';
import { CompressorCore, gainQ15 } from './dynamics-core.mjs';
import { panPoints } from './live-pan-auto.mjs';
import { curveAt } from './clientside-control-curves.mjs';

/**
 * The threshold of every sample when it is automated (auto(...), lfo, env, or the points of a control), as the server (Effects.cs thresholdAutomated): the curve joins its points
 * in straight lines per sample, is held between -60 and 0 and each point is turned into the limit (table gain * 32767 >> 15) before the lines are drawn. Null for a plain threshold.
 */
export function thresholdLimits(value, samplesPerBar, rate, firstBar, length) {
  const points = panPoints(value, samplesPerBar, rate);
  if (!points) return null;
  // the points are turned into limits first and the straight lines join the limits (a line in amplitude, not in dB), as the server does (valueOf of Automation.CurveAt)
  return curveAt(points, Math.max(1, firstBar), samplesPerBar, length, (v) => Math.floor(gainQ15(Math.max(-60, Math.min(0, v))) * 32767 / 32768));
}

/** AMPLE-linked compressor. Float envelope/gain math approximates the server Q15 path. The maths is CompressorCore (dynamics-core.mjs), also run by the live AudioWorklet. */
export async function compressorAudio(audio, params, keyAudio, signal, limits = null) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, length = audio.channels[0].length, channels = audio.channels;
  const core = new CompressorCore(rate);
  if (keyAudio?.sampleRate && keyAudio.sampleRate !== rate) core.setKeyRate(keyAudio.sampleRate);
  core.configure(params);
  const output = channels.map(() => new Float32Array(length));
  for (let from = 0; from < length; from += 0x10000) {
    signal?.throwIfAborted(); await pause();
    core.process(channels, output, from, Math.min(length, from + 0x10000), keyAudio?.channels || null, limits);
  }
  return { sampleRate: rate, channels: output };
}
