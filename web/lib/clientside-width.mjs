import { pause } from './yield-slice.mjs';

/** Applies AMPLE's mid/side width matrix to stereo input; mono remains unchanged. */
export async function widthAudio(audio, params, signal) {
  if (params.enabled === 'off' || audio.channels.length < 2) return audio;
  const parsed = Number.parseInt(params.width, 10), width = Math.max(0, Math.min(200, Number.isFinite(parsed) ? parsed : 100));
  const left = new Float32Array(audio.channels[0].length), right = new Float32Array(audio.channels[1].length);
  for (let start = 0; start < left.length; start += 65536) {
    signal?.throwIfAborted();
    const end = Math.min(left.length, start + 65536);
    for (let i = start; i < end; i++) {
      const mid = (audio.channels[0][i] + audio.channels[1][i]) / 2, side = (audio.channels[0][i] - audio.channels[1][i]) * width / 200;
      left[i] = mid + side; right[i] = mid - side;
    }
    await pause();
  }
  return { sampleRate: audio.sampleRate, channels: [left, right] };
}
