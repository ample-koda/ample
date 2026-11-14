// The bar range of a take review as typed in two number fields (1-based, both inclusive). Pure, so the rules are testable.

/** Parse a field: a whole number, or null when it is empty or not a number. */
export function parseBar(text) {
  const s = String(text ?? '').trim();
  if (s === '') return null;
  const n = Number(s);
  return Number.isFinite(n) ? Math.round(n) : null;
}

/**
 * The range after one field was edited. An unusable value leaves the range as it was; a number is clamped to 1..bars;
 * the edited field wins, so the other one is pulled along when the range would turn upside down.
 * @param {'from'|'to'} which @param {string} text @param {{ from: number, to: number }} current @param {number} bars
 */
export function editRange(which, text, current, bars) {
  const max = Math.max(1, Math.floor(bars) || 1), n = parseBar(text);
  let { from, to } = current;
  from = Math.min(max, Math.max(1, from)); to = Math.min(max, Math.max(from, to));
  if (n === null) return { from, to };
  const v = Math.min(max, Math.max(1, n));
  if (which === 'from') { from = v; to = Math.max(to, v); } else { to = v; from = Math.min(from, v); }
  return { from, to };
}
