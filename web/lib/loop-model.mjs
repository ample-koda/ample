// The arithmetic of looped playback in the sequencer workspace (pure, tested without a browser): bars and seconds, where the playhead is, when the loop
// comes round again. A "loop" is a stretch of the take, [start, end] in seconds; without one the whole take is the loop. @ample:loop

/** The length of a bar in seconds (four beats). */
import { barBeats } from './meter.mjs';
export const barSeconds = (tempo) => (barBeats() * 60) / Math.max(1, Number(tempo) || 120);

/** The stretch of a take that bars `at` to `at + bars - 1` cover, in seconds, kept inside the take. */
export function rangeSeconds(range, tempo, duration) {
  if (!range || !(range.bars >= 1)) return null;
  const bar = barSeconds(tempo);
  const start = Math.max(0, (range.at - 1) * bar);
  const end = Math.min(duration || Infinity, (range.at - 1 + range.bars) * bar);
  return end - start > 0.05 ? { start, end } : null;
}

/** Seconds into the take to the bar number with a fraction (bar 1 is the start). */
export const barAt = (seconds, tempo) => 1 + Math.max(0, seconds) / barSeconds(tempo);

/**
 * Where the playhead is, `elapsed` seconds after it started at `offset`, in a loop [start, end] (or none: it plays to the end of the take and stops there).
 * @returns {number} seconds into the take
 */
export function positionAt(elapsed, offset, loop, duration) {
  if (!loop) return Math.min(duration, offset + elapsed);
  const length = loop.end - loop.start;
  const from = offset >= loop.end || offset < 0 ? loop.start : offset;
  const first = loop.end - from;
  if (elapsed < first) return from + elapsed;
  return loop.start + ((elapsed - first) % length);
}

/** Seconds from now until the loop comes round again (the next time the playhead is at the end of the loop), for a swap that must not be heard. */
export function untilNextReturn(elapsed, offset, loop) {
  if (!loop) return 0;
  const length = loop.end - loop.start;
  const from = offset >= loop.end || offset < 0 ? loop.start : offset;
  const first = loop.end - from;
  if (elapsed < first) return first - elapsed;
  return length - ((elapsed - first) % length);
}

/** Turns a drag on the ruler into a bar range (at least one bar), or null when it was a click. `pxPerBar` is the width of a bar on the screen. */
export function rangeFromDrag(startX, endX, pxPerBar, minPixels = 6) {
  if (Math.abs(endX - startX) < minPixels) return null;
  const a = Math.min(startX, endX), b = Math.max(startX, endX);
  const first = Math.floor(a / pxPerBar) + 1, last = Math.max(first, Math.ceil(b / pxPerBar));
  return { at: first, bars: last - first + 1 };
}

/** The bar a click on the ruler points at, with a fraction of a bar kept to a quarter. */
export const barOfClick = (x, pxPerBar) => 1 + Math.round((Math.max(0, x) / pxPerBar) * 4) / 4;
