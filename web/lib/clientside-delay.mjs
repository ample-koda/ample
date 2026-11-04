import { pause } from './yield-slice.mjs';
const value = (text, fallback) => {
  const parsed = Number.parseFloat(String(text ?? '').replace(/ms|%/gi, '').trim());
  return Number.isFinite(parsed) ? parsed : fallback;
};

/** Applies AMPLE's bounded integer-delay topology over decoded buffers; floats keep the result approximate. */
export async function delayAudio(audio, params, signal) {
  if (params.enabled === 'off') return audio;
  const frames = audio.channels[0]?.length || 0, length = Math.max(1, Math.floor(Math.max(1, Math.min(2000, value(params.time, 250))) * audio.sampleRate / 1000));
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
