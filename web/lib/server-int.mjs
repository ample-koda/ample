// A whole-number parameter as the server reads it (Effects.cs Read): the leading sign and digits, the rest is ignored, so "5.5 ms" is 5 and "1.2 kHz" is 1. Not a number: the fallback.
/** The leading whole number of a parameter text (or the number itself), truncated like the server; `fallback` when there is none. */
export function serverInt(value, fallback) {
  const match = /^\s*([+-]?[0-9]+)/.exec(String(value ?? '').replace(/^"|"$/g, ''));
  return match ? Number.parseInt(match[1], 10) : fallback;
}
