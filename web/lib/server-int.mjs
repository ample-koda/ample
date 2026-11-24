// A whole-number parameter as the server reads it (Effects.cs Read): the leading sign and digits, the rest is ignored, so "5.5 ms" is 5 and "1.2 kHz" is 1. Not a number: the fallback.
/** The leading whole number of a parameter text (or the number itself), truncated like the server; `fallback` when there is none. */
export function serverInt(value, fallback) {
  const match = /^\s*([+-]?[0-9]+)/.exec(String(value ?? '').replace(/^"|"$/g, ''));
  return match ? Number.parseInt(match[1], 10) : fallback;
}

/**
 * A level in whole decibels as the server reads it (Effects.cs ReadRoundedDb): the leading number with a decimal part rounded to the nearest whole decibel,
 * halves away from zero ("-2.8 dB" is -3, "-0.5" is -1); a point as the first character or no number at all gives `fallback`. Used for the mixer gains and
 * the effect levels of the one table (Ample.Language.WholeDecibels, studio-math.js wholeDb).
 */
export function serverRoundedDb(value, fallback) {
  const text = String(value ?? '').trim().replace(/^"|"$/g, '').trim();
  if (text.startsWith('.')) return fallback;
  const match = /^[+-]?\d*(?:\.\d*)?/.exec(text);
  const number = match && /\d/.test(match[0]) ? Number(match[0]) : Number.NaN;
  if (!Number.isFinite(number) || Math.abs(number) >= 1e6) return fallback;
  return Math.sign(number) * Math.round(Math.abs(number));
}
