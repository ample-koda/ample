import { pause } from './yield-slice.mjs';
const number = (value, fallback) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/ms|%/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** AMPLE phaser: cascaded first-order all-pass stages driven by its triangle sweep. */
export async function phaserAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const stages = Math.max(2, Math.min(8, Math.trunc(number(params.stages, 4))));
  const period = Math.max(2, Math.trunc(Math.max(100, Math.min(10000, number(params.period, 2500))) * audio.sampleRate / 1000));
  const mix = Math.max(0, Math.min(100, number(params.mix, 50))) / 100;
  const channels = [];
  for (const input of audio.channels) {
    const output = new Float32Array(input.length), previousInput = new Float64Array(stages), previousOutput = new Float64Array(stages);
    for (let start = 0; start < input.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(input.length, start + 65536);
      for (let i = start; i < end; i++) {
        const phase = i % period, half = Math.max(1, Math.floor(period / 2));
        const triangle = Math.trunc(phase < half ? phase * 32767 / half : (period - phase) * 32767 / half);
        const coefficient = (3277 + Math.trunc(triangle * 26000 / 32768)) / 32768;
        let value = input[i];
        for (let stage = 0; stage < stages; stage++) {
          const filtered = -coefficient * value + previousInput[stage] + coefficient * previousOutput[stage];
          previousInput[stage] = value;
          previousOutput[stage] = filtered;
          value = filtered;
        }
        output[i] = input[i] * (1 - mix) + value * mix;
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: audio.sampleRate, channels };
}
