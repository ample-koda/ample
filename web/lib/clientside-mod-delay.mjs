import { pause } from './yield-slice.mjs';

/**
 * Shared implementation of AMPLE's triangle-modulated, linearly interpolated delay, with the geometry of the server (Effects.cs ModDelay): base and depth are whole samples
 * (depth 5 ms at 44.1 kHz is 220 samples, not 220.5: half a sample off made the chorus -18 dB from the render), the triangle is the integer one (0..32767), the delay is kept in
 * 1/256 sample and is never below 2 samples. The signal itself stays in floats.
 */
/** With `stereoQuarter` the second channel sweeps a quarter of a period ahead (the chorus, so a stereo sound gets width). */
export async function modDelayAudio(audio, { baseMs, depthMs, periodMs, feedback, mix, stereoQuarter = false }, signal) {
  const rate = audio.sampleRate, baseSamples = Math.trunc(baseMs * rate / 1000), depthSamples = Math.trunc(depthMs * rate / 1000), period = Math.max(2, Math.floor(periodMs * rate / 1000)), mixAmount = Math.max(0, Math.min(100, mix)) / 100;
  const feedbackAmount = Math.max(0, Math.min(90, feedback)) / 100, channels = [];
  for (const [channelIndex, input] of audio.channels.entries()) {
    const output = new Float32Array(input.length), write = new Float32Array(input.length), offset = stereoQuarter && channelIndex === 1 ? Math.floor(period / 4) : 0;
    for (let start = 0; start < input.length; start += 65536) {
      signal?.throwIfAborted();
      const end = Math.min(input.length, start + 65536);
      for (let i = start; i < end; i++) {
        const phase = (i + offset) % period, half = Math.max(1, Math.floor(period / 2));
        const triangle = Math.trunc(phase < half ? phase * 32767 / half : (period - phase) * 32767 / half);
        const delayQ8 = Math.max(2 * 256, baseSamples * 256 + Math.floor((triangle - 16384) * depthSamples * 256 / 16384));
        const positionQ8 = i * 256 - delayQ8;
        let delayed = 0;
        if (positionQ8 >= 0) {
          const index = Math.floor(positionQ8 / 256), fraction = (positionQ8 - index * 256) / 256, source = feedbackAmount > 0 ? write : input;
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
