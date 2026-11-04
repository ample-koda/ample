// The one-pole low pass of the effects, shared by the browser previews: its coefficient puts the -3 dB point where it is asked
// (the server's AudioEffects.CutoffCoefficient). The older guess x / (1 + x) landed too low at high frequencies (6 kHz asked gave about 4.3 kHz).

/** The coefficient (0 to 1) of a one-pole low pass with its -3 dB point near `hz`: 1 - e^(-2 pi hz / rate). */
export function cutoffAlpha(hz, rate) {
  return 1 - Math.exp(-2 * Math.PI * Math.max(1, Math.min(hz, rate / 2 - 1)) / rate);
}

/** The same coefficient in Q15 exactly as the server computes it (an integer series), for the previews that match the server sample for sample. */
export function cutoffCoefficient(hz, rate) {
  const clamped = Math.max(1, Math.min(Math.trunc(hz), Math.trunc(rate / 2) - 1));
  const x = BigInt(Math.trunc(105414357 * clamped / rate));
  let sum = 1n << 24n, term = 1n << 24n;
  for (let k = 1n; k < 40n && term > 0n; k++) {
    term = (term * x / k) >> 24n;
    sum += term;
  }
  return 32768 - Number((32768n << 24n) / sum);
}
