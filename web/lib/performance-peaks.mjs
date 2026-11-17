// The shape of a clip for its pad (plan/performance-live.md, phase 3): the loudest value of every column of a sound, and the loop position as a fraction. Pure, so node --test checks it.

/** `columns` peaks, 0..1: the largest absolute value of every channel in each column of the sound. */
export function peaksOf(channels, columns) {
  const count = Math.max(1, Math.floor(columns));
  const peaks = new Float32Array(count);
  const length = channels?.[0]?.length ?? 0;
  if (!length) return peaks;
  const step = length / count;
  for (const channel of channels) {
    for (let c = 0; c < count; c++) {
      const from = Math.floor(c * step), to = Math.max(from + 1, Math.floor((c + 1) * step));
      let max = peaks[c];
      // a long column is looked at on a grid of at most 512 samples: the shape is the same and a long loop stays quick
      const stride = Math.max(1, Math.floor((to - from) / 512));
      for (let i = from; i < to && i < length; i += stride) { const v = Math.abs(channel[i]); if (v > max) max = v; }
      peaks[c] = Math.min(1, max);
    }
  }

  return peaks;
}

/** The peaks scaled so that the loudest column is one (a quiet clip still shows its shape); silence stays silence. */
export function scalePeaks(peaks) {
  let max = 0;
  for (const p of peaks) if (p > max) max = p;
  return max > 0 ? Float32Array.from(peaks, (p) => p / max) : Float32Array.from(peaks);
}

/** Where in a loop of `duration` seconds the sound is `elapsed` seconds after it started, 0..1 (a clip that has not started is at the start). */
export function loopFraction(elapsed, duration) {
  if (!(duration > 0) || !Number.isFinite(elapsed) || elapsed <= 0) return 0;
  return (elapsed % duration) / duration;
}
