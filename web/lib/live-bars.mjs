/** The live preview renders at most 90% of the 128 MiB working-set net of the offline renderers; the worst voice (the synth: 12 bytes per frame and channel, 2 channels) sets the bar cap. */
export const LIVE_BYTES = 128 * 1024 * 1024 * 0.9, LIVE_BYTES_PER_FRAME = 24;
/** The bars the live chain plays: all of the song, or the bars that fit (at least 1). */
export function fitLiveBars(songBars, samplesPerBar) {
  return Math.min(songBars, Math.max(1, Math.floor(LIVE_BYTES / LIVE_BYTES_PER_FRAME / samplesPerBar)));
}
/**
 * The first bar (1-based) of the window of `liveBars` bars that the live chain renders when the song is longer than that: `wanted` (the loop range start the user set on the chain bar),
 * kept inside the song so the window is always full. A song that fits entirely has no window: 1.
 */
export function windowStart(songBars, liveBars, wanted = 1) {
  if (!(liveBars < songBars)) return 1;
  const last = songBars - liveBars + 1, bar = Math.trunc(Number(wanted));
  return Math.min(last, Math.max(1, Number.isFinite(bar) ? bar : 1));
}
/** The song bar (1-based) that `seconds` into the window is in, when the window starts at bar `first`. */
export const barAt = (first, seconds, barSeconds) => first + Math.floor(seconds / barSeconds + 1e-9);
/** Seconds into the song at `seconds` into the window: what a gain curve, a control or a follow series is read at. */
export const songSeconds = (first, seconds, barSeconds) => seconds + (first - 1) * barSeconds;
/**
 * The notes of a whole-file (seconds) render cut to the window: those that sound in [(first-1)*barSeconds, +bars*barSeconds), moved to the start of the window.
 * With first = 1 the notes are returned as they are (a song that fits entirely is unchanged).
 */
export function windowNotes(notes, first, bars, barSeconds) {
  if (first <= 1) return notes;
  const from = (first - 1) * barSeconds, to = from + bars * barSeconds;
  return notes.filter((n) => n.end > from && n.start < to).map((n) => ({ ...n, start: Math.max(0, n.start - from), end: Math.min(to, n.end) - from }));
}
/** What the badge says when the live preview plays only a window of a long song, or ''. `first` is the first bar of the window. */
export const liveBarsNote = (songBars, liveBars, first = 1) => (liveBars < songBars ? `previewing bars ${first}-${first + liveBars - 1} of ${songBars}` : '');
