// A range of bars typed in two fields (first bar, number of bars): the range to audition, or null when the fields do not make one. Pure, so node --test checks it.
export function rangeFromFields(fromText, barsText, limit = 999) {
  const at = Math.round(Number(String(fromText).trim())), bars = Math.round(Number(String(barsText).trim()));
  if (!Number.isFinite(at) || !Number.isFinite(bars) || at < 1 || bars < 1 || at > limit) return null;
  return { at, bars: Math.min(bars, limit - at + 1) };
}
