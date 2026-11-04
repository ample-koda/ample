/**
 * Where the joins of a loop are, from the peaks of its loop-region (min and max pairs): the stretches of bins that are not silent.
 * Returns [{ from, to }] in seconds; seconds is the length of the whole audio.
 */
export function seamSpans(peaks, seconds, floor = 1e-3) {
  const n = Math.floor((peaks?.length ?? 0) / 2);
  const spans = [];
  let from = -1;
  for (let i = 0; i <= n; i++) {
    const loud = i < n && (Math.abs(peaks[i * 2]) > floor || Math.abs(peaks[i * 2 + 1]) > floor);
    if (loud && from < 0) from = i;
    else if (!loud && from >= 0) { spans.push({ from: (from / n) * seconds, to: (i / n) * seconds }); from = -1; }
  }
  return spans;
}

/** The seconds of the joins as text for a tooltip ("joins: 2.5–5.5 s, 7.5–10.5 s"), at most six, then "and N more"; empty when there are none. */
export function seamText(spans) {
  if (!spans.length) return '';
  const f = (x) => (Math.round(x * 10) / 10).toString();
  const shown = spans.slice(0, 6).map((s) => `${f(s.from)}–${f(s.to)} s`).join(', ');
  return `Loop seams (${spans.length}): ${shown}${spans.length > 6 ? ` and ${spans.length - 6} more` : ''}`;
}
