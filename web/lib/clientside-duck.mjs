import { pause } from './yield-slice.mjs';
import { barBeats, stepsPerBar } from './meter.mjs';
const number = (value, fallback, min, max) => {
  const parsed = Number.parseFloat(String(value ?? '').replace(/dBFS|dB|%|ms/gi, '').trim());
  return Math.max(min, Math.min(max, Number.isFinite(parsed) ? parsed : fallback));
};

/** Applies AMPLE's pattern-triggered duck envelope using the project tempo. */
export async function duckAudio(audio, params, tempo, signal) {
  if (params.enabled === 'off') return audio;
  const rate = audio.sampleRate, depth = number(params.depth, 10, 0, 40), release = Math.max(1, Math.floor(number(params.release, 160, 10, 2000) * rate / 1000));
  const pattern = String(params.pattern || 'x...x...x...x...').replace(/[|\s]/g, '') || 'x...x...x...x...';
  const samplesPerBar = Math.floor(barBeats() * 60 * rate / Math.max(1, Number(tempo) || 120)), stepLength = Math.max(1, Math.floor(samplesPerBar / stepsPerBar()));
  const attack = Math.max(1, Math.floor(rate * 4 / 1000)), tail = Math.max(release, attack + 1), floor = 10 ** (-depth / 20);
  const channels = audio.channels.map(() => new Float32Array(audio.channels[0]?.length || 0));
  for (let start = 0; start < channels[0].length; start += 65536) {
    signal?.throwIfAborted();
    const end = Math.min(channels[0].length, start + 65536);
    for (let i = start; i < end; i++) {
      let gain = 1;
      const latest = Math.floor(i / stepLength), checks = Math.ceil(tail / stepLength) + 1;
      for (let back = 0; back < checks; back++) {
        const step = latest - back;
        if (step < 0 || pattern[step % pattern.length] !== 'x') continue;
        const elapsed = i - step * stepLength;
        if (elapsed >= tail) continue;
        const hitGain = elapsed < attack ? 1 - (1 - floor) * elapsed / attack : floor + (1 - floor) * (elapsed - attack) / Math.max(1, tail - attack);
        gain = Math.min(gain, hitGain);
      }
      for (let c = 0; c < audio.channels.length; c++) channels[c][i] = audio.channels[c][i] * gain;
    }
    await pause();
  }
  return { sampleRate: rate, channels };
}
