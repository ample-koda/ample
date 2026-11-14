// The pure part of the number fields of the properties panel: which parameters are numbers with a range, and reading what was typed.
// Tested in web/lib/test/numfield.test.mjs.
// @ample:number-fields

/** Ranges of numeric parameters that a template does not give itself, by parameter name: [min, max, step]. */
export const RANGES = {
  swing: [0, 60, 1],
  steps: [1, 64, 1],
  humanize: [0, 100, 1],
  stagger: [0, 80, 1],
  gain: [-24, 12, 1],
  steps: [1, 100, 1],
  bars: [1, 128, 1],
};

/** The range of a parameter: the template's own (min and max) wins, then the table above, else null (a typed field without a slider). */
export function rangeOf(name, meta) {
  if (meta && Number.isFinite(meta.min) && Number.isFinite(meta.max)) return [meta.min, meta.max, meta.step ?? 1];
  return RANGES[name] ?? null;
}

/** Places a floating slider above a field only when the panel edge leaves too little room below. */
export function sliderPlacement(field, panel, height) {
  return field.bottom + height > panel.bottom && field.top - height > panel.top ? 'above' : 'below';
}

/** True when the parameter should be a number field: the template says number, or it is in the table. */
export const isNumeric = (name, meta) => meta?.kind === 'number' || meta?.type === 'number' || Object.hasOwn(RANGES, name);

/** Removes a trailing unit ("-1 dB" and "12%" with unit dB or %) from a written value. */
export function stripUnit(text, unit) {
  const raw = String(text ?? '').trim();
  if (!unit) return raw;
  return raw.toLowerCase().endsWith(unit.toLowerCase()) ? raw.slice(0, raw.length - unit.length).trim() : raw;
}

/** Writes a number with its unit the way songs write it: "12%" without a space, "-1 dB" with one. A unit with a space in it ("GM program") is only a label of the field, never part of the song text. */
export const withUnit = (text, unit) => (!unit || /\s/.test(unit) ? String(text) : unit === '%' ? `${text}%` : `${text} ${unit}`);

/**
 * Reads what was typed. Returns { value, text, clamped, error }:
 * value is the number, text how it is written back (without the unit), clamped says it was moved into the range, error a plain sentence when it is no number.
 */
export function readNumber(typed, range, unit) {
  const raw = stripUnit(typed, unit).replace(',', '.');
  if (raw === '' || !/^[-+]?(\d+(\.\d*)?|\.\d+)$/.test(raw)) return { value: null, text: null, clamped: false, error: `"${String(typed ?? '').trim()}" is not a number` };
  let value = Number(raw);
  let clamped = false;
  if (range) {
    const [min, max] = range;
    if (value < min) { value = min; clamped = true; }
    if (value > max) { value = max; clamped = true; }
  }
  return { value, text: String(value), clamped, error: null };
}

/** Splits a channels value ("0 0 -2 0", "c c l r") into exactly count words: missing ones get the fallback, extra ones are dropped. */
export function splitChannels(text, count, fallback) {
  const words = String(text ?? '').trim().split(/\s+/).filter(Boolean);
  return Array.from({ length: count }, (_, i) => words[i] ?? fallback);
}

/** How many lanes a channels field shows: the declared count, or more when the value already holds more words. */
export const channelCount = (text, declared = 4) => Math.max(declared || 4, String(text ?? '').trim().split(/\s+/).filter(Boolean).length);

/**
 * A level in dB as the renderer reads it. The renderer only reads whole numbers (a fraction would play as 0 dB),
 * so a typed number is rounded to a whole one and a text that is not a number is kept as it is.
 */
export function wholeDb(text) {
  const t = String(text ?? '').trim();
  const n = Number(t.replace(/\s*dB$/i, ''));
  return t !== '' && Number.isFinite(n) ? String(Math.round(n)) : t;
}

/** The words of a channels value written back as one text, each number with its unit when it has one ("-2 dB" is written as "-2": the unit is part of the field, not of each channel). */
export const joinChannels = (words) => words.join(' ');
