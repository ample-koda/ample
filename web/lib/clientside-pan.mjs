import { pause } from './yield-slice.mjs';
import { panGains } from './mix-laws.mjs';
import { panPoints, panCurve } from './live-pan-auto.mjs';
import { barBeats } from './meter.mjs';

/**
 * Applies the pan of the server: a table of gains, stereo balanced and mono spread to stereo. A pan written as auto(...), lfo(...) or env(...) is the server's curve (live-pan-auto.mjs):
 * the gains of the points joined by straight lines, counted from the start of the song (`firstBar` is the first bar of the audio).
 */
export async function panAudio(audio, params, signal, tempo = 120, firstBar = 1) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, samplesPerBar = Math.floor(rate * (barBeats() * 60) / (Number(tempo) || 120)), points = panPoints(params.pan, samplesPerBar, rate);
  if (points) {
    const stereo = audio.channels.length > 1, from = stereo ? audio.channels.slice(0, 2) : [audio.channels[0], audio.channels[0]], curves = ['left', 'right'].map((side) => panCurve(points, Math.max(1, firstBar), samplesPerBar, side, stereo)), result = [];
    for (let channel = 0; channel < 2; channel++) {
      const source = from[channel], output = new Float32Array(source.length), curve = curves[channel];
      for (let start = 0; start < source.length; start += 65536) {
        signal?.throwIfAborted();
        const end = Math.min(source.length, start + 65536);
        for (let i = start; i < end; i++) output[i] = source[i] * curve(i);
        await pause();
      }
      result.push(output);
    }
    return { sampleRate: rate, channels: result };
  }
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
