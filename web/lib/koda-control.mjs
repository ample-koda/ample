// Koda follows a control: while a song plays, the curve of its control lane (a car that passes) makes Koda sway a little more.
// The curve is what GET /api/takes/{id}/control gives: { firstBar, unit, rate (values per second), values }.

/**
 * The value of a control curve at a time in the song, from 0 to 1 (a general control is 0 to 1000; a typed one is scaled to the range it has).
 * @param {{values: number[], rate: number, firstBar: number, unit?: string|null}} curve The curve.
 * @param {number} seconds The time in the song.
 * @param {number} secondsPerBar The length of a bar.
 * @returns {number} 0 to 1.
 */
export function controlAt(curve, seconds, secondsPerBar) {
  const values = curve?.values;
  if (!values?.length || !curve.rate) return 0;
  const index = Math.floor((seconds - (curve.firstBar - 1) * secondsPerBar) * curve.rate);
  if (index < 0 || index >= values.length) return 0;
  if (!curve.unit) return Math.min(1, Math.max(0, values[index] / 1000));
  let low = Infinity, high = -Infinity;
  for (const v of values) { if (v < low) low = v; if (v > high) high = v; }
  return high > low ? (values[index] - low) / (high - low) : 0;
}
