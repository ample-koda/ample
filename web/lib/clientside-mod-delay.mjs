import { pause } from './yield-slice.mjs';

/** Shared float implementation of AMPLE's triangle-modulated, linearly interpolated delay. */
/** With `stereoQuarter` the second channel sweeps a quarter of a period ahead (the chorus, so a stereo sound gets width). */
export async function modDelayAudio(audio, { baseMs, depthMs, periodMs, feedback, mix, stereoQuarter = false }, signal) {
  const rate = audio.sampleRate, period = Math.max(2, Math.floor(periodMs * rate / 1000)), mixAmount = Math.max(0, Math.min(100, mix)) / 100;
  const feedbackAmount = Math.max(0, Math.min(90, feedback)) / 100, channels = [];
  for (const [channelIndex, input] of audio.channels.entries()) {
    const output = new Float32Array(input.length), write = new Float32Array(input.length), offset = stereoQuarter && channelIndex === 1 ? Math.floor(period / 4) : 0;
    for (let start = 0; start < input.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(input.length, start + 65536);
      for (let i = start; i < end; i++) {
        const phase = (i + offset) % period, half = Math.max(1, Math.floor(period / 2));
        const triangle = phase < half ? phase / half : (period - phase) / half;
        const delay = Math.max(2, (baseMs + ((triangle * 2 - 1) * depthMs)) * rate / 1000);
        const position = i - delay;
        let delayed = 0;
        if (position >= 0) {
          const index = Math.floor(position), fraction = position - index, source = feedbackAmount > 0 ? write : input;
          delayed = source[index] * (1 - fraction) + source[Math.min(index + 1, i)] * fraction;
        }
        write[i] = input[i] + delayed * feedbackAmount;
        output[i] = input[i] * (1 - mixAmount) + delayed * mixAmount;
      }
      await pause();
    }
    channels.push(output);
  }
  return { sampleRate: rate, channels };
}
