import { pause } from './yield-slice.mjs';
import { serverInt } from './server-int.mjs';
import { barBeats } from './meter.mjs';

const CYCLES = { '1/1': 1, '1/2': 2, '1/4': 4, '1/8': 8, '1/16': 16, '1/32': 32, '1/4t': 6, '1/8t': 12, '1/16t': 24 };   // note values per bar, as the server's FilterLfoCycles
/** The echo time in samples when `sync` locks it to the tempo (a whole number of samples of the bar, as the server), or 0 when sync is off. */
export function syncSamples(params, rate, tempo = 120) {
  const cycles = CYCLES[String(params?.sync ?? 'off').trim()];
  if (!cycles) return 0;
  const samplesPerBar = Math.floor(barBeats() * 60 * rate * 1000 / Math.round((Number(tempo) || 120) * 1000));
  // a note value is a part of a whole note (four beats) in any meter, as the server (delay 2): 1/8 is an eighth note in 3/4 too
  return Math.max(1, Math.round(samplesPerBar / (cycles * barBeats() / 4)));
}
// a whole number as the server reads it (Effects.cs Read): "187.5 ms" is 187 and "37.5%" is 37 there, so the preview reads them the same way
const value = (text, fallback) => serverInt(text, fallback);

/** Applies AMPLE's bounded integer-delay topology over decoded buffers; floats keep the result approximate. */
export async function delayAudio(audio, params, signal, tempo = 120) {
  if (params.enabled === 'off') return audio;
  const frames = audio.channels[0]?.length || 0, length = syncSamples(params, audio.sampleRate, tempo) || Math.max(1, Math.floor(Math.max(1, Math.min(2000, value(params.time, 250))) * audio.sampleRate / 1000));
  const feedback = Math.max(0, Math.min(90, value(params.feedback, 35))) / 100, mix = Math.max(0, Math.min(100, value(params.mix, 30))) / 100, channels = [];
  for (const source of audio.channels) {
    const echo = new Float32Array(frames), output = new Float32Array(frames);
    for (let start = 0; start < frames; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(frames, start + 65536);
      for (let i = start; i < end; i++) {
        if (i >= length) echo[i] = source[i - length] + echo[i - length] * feedback;
        output[i] = Math.max(-1, Math.min(1, source[i] + echo[i] * mix));
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}
