// Time stretching of mono 16-bit audio without changing the pitch (WSOLA: overlap-add of Hann-windowed frames, each moved a little to
// the place where it continues the previous one best). Integer in, integer out; plain floating point inside.

/** Finds where the sound ends: the last sample above a small part of the loudest one (a quiet tail of noise does not count). */
export function soundEnd(x, share = 0.04) {
  let peak = 0;
  for (let i = 0; i < x.length; i++) { peak = Math.max(peak, Math.abs(x[i])); }
  const limit = peak * share;
  for (let i = x.length - 1; i >= 0; i--) { if (Math.abs(x[i]) > limit) { return i + 1; } }
  return 0;
}

/** Makes the samples factor times as long (factor above 1 is slower) keeping the pitch. */
export function stretch(x, factor, rate) {
  const N = Math.max(64, Math.round(rate * 0.04) & ~1), Hs = N >> 1, tol = N >> 2;
  const len = Math.round(x.length * factor);
  const out = new Float64Array(len + N), norm = new Float64Array(len + N);
  const win = Float64Array.from({ length: N }, (_, i) => 0.5 - 0.5 * Math.cos((2 * Math.PI * (i + 0.5)) / N));
  let prev = 0;
  for (let k = 0; ; k++) {
    const syn = k * Hs;
    if (syn >= len) { break; }
    let ana = Math.round(syn / factor);
    if (k > 0) {
      let best = -Infinity, at = ana;
      const want = prev + Hs;
      for (let d = -tol; d <= tol; d++) {
        const c = ana + d;
        if (c < 0 || c + Hs > x.length || want + Hs > x.length) { continue; }
        let corr = 0;
        for (let i = 0; i < Hs; i += 2) { corr += x[c + i] * x[want + i]; }
        if (corr > best) { best = corr; at = c; }
      }
      ana = at;
    }
    for (let i = 0; i < N; i++) {
      const s = ana + i < x.length ? x[ana + i] : 0;
      out[syn + i] += s * win[i];
      norm[syn + i] += win[i];
    }
    prev = ana;
  }
  const result = new Int16Array(len);
  for (let i = 0; i < len; i++) { result[i] = Math.max(-32768, Math.min(32767, Math.round(norm[i] > 1e-3 ? out[i] / norm[i] : 0))); }
  return result;
}
