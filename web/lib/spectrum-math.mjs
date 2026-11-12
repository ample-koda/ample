// Spectrum and scope maths for the live chain (local-preview chunk 5.3). Pure, so the shapes can be tested without a browser.

/** Log-spaced centre frequencies from `low` to `high` Hz. */
export function bandCentres(count = 96, low = 20, high = 20000) {
  return Array.from({ length: count }, (_, index) => low * (high / low) ** (index / (count - 1)));
}

/**
 * Turns linear-frequency dB bins (an AnalyserNode's getFloatFrequencyData) into one dB value per log band, smoothed over `octave` (1/6 by default): the power of the bins inside
 * each band edge is averaged. dB per bin in, dB per band out; an empty band takes the nearest bin.
 */
export function logBands(bins, rate, centres, octave = 1 / 6) {
  const hz = rate / 2 / (bins.length - 1);
  return centres.map((centre) => {
    const lowBin = Math.max(0, Math.floor(centre * 2 ** (-octave / 2) / hz)), highBin = Math.min(bins.length - 1, Math.ceil(centre * 2 ** (octave / 2) / hz));
    if (highBin < lowBin) return bins[Math.min(bins.length - 1, Math.round(centre / hz))];
    let power = 0, n = 0;
    for (let bin = lowBin; bin <= Math.max(lowBin, highBin); bin++) { power += 10 ** (bins[bin] / 10); n++; }
    return 10 * Math.log10(power / n);
  });
}

/** Peak hold: each value falls by `fall` dB per call but never below the new reading. */
export function peakHold(previous, current, fall = 0.6) {
  return current.map((value, index) => Math.max(value, (previous?.[index] ?? -Infinity) - fall));
}

/** The index where a scope trace starts: the first rising zero crossing in the first half, so a steady tone does not wander. 0 if none. */
export function scopeStart(samples) {
  const limit = Math.floor(samples.length / 2);
  for (let index = 1; index < limit; index++) if (samples[index - 1] < 0 && samples[index] >= 0) return index;
  return 0;
}

/** Maps dB (floor..0) to a 0..1 height. */
export const dbHeight = (db, floor = -90, top = 0) => Math.max(0, Math.min(1, (db - floor) / (top - floor)));
