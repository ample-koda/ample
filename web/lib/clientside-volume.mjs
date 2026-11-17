import { barBeats } from './meter.mjs';
import { serverInt } from './server-int.mjs';
import { gainQ15Wide, volumeCurve } from './volume-auto.mjs';

const wholeDb = (text) => Math.max(-60, Math.min(48, serverInt(text, 0)));

/**
 * Applies the volume block of the server (VolumeRenderer.cs): the gain in whole dB from the Q15 table, an automated gain (auto, lfo, env, fadein, fadeout) as the server's curve per sample
 * (volume-auto.mjs; `tempo` and `firstBar`, the first bar of the audio, place it in the song), the mono fold and the channel mutes.
 */
export function volumeAudio(audio, params, tempo = 120, firstBar = 1) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, samplesPerBar = Math.floor(rate * (barBeats() * 60) / (Number(tempo) || 120));
  const curve = volumeCurve(params.gain, samplesPerBar, rate, firstBar);
  const left = audio.channels[0], right = audio.channels[1] || left, channels = [new Float32Array(left.length), new Float32Array(left.length)];
  const gain = curve ? 1 : gainQ15Wide(wholeDb(params.gain)) / 32768, leftBase = gainQ15Wide(wholeDb(params.left_gain)) / 32768, rightBase = gainQ15Wide(wholeDb(params.right_gain)) / 32768;
  for (let i = 0; i < left.length; i++) {
    const g = curve ? curve(i) : gain, l = params.mono === 'on' ? (left[i] + right[i]) / 2 : left[i], r = params.mono === 'on' ? (left[i] + right[i]) / 2 : right[i];
    channels[0][i] = params.left === 'off' ? 0 : Math.max(-1, Math.min(1, l * g * leftBase));
    channels[1][i] = params.right === 'off' ? 0 : Math.max(-1, Math.min(1, r * g * rightBase));
  }
  return { sampleRate: rate, channels };
}
