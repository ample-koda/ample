// The clip a drag on an empty stretch of a Playlist line draws: from the bar where it began to the bar where it is now, in either direction, at least one bar.
// Pure, so node --test checks it.

/**
 * @param {number} first the bar where the drag began
 * @param {number} here the bar under the pointer now
 * @returns {{ start: number, length: number }} the first bar (never before bar 1) and the length in bars (never below 1)
 */
export function drawnRange(first, here) {
  const a = Math.max(1, Math.round(first) || 1), b = Math.max(1, Math.round(here) || 1);
  const start = Math.min(a, b);
  return { start, length: Math.max(a, b) - start + 1 };
}

/** A press and release without a real movement is a click, not a drawing: it makes no clip. */
export const isDrag = (fromX, toX, minPixels = 4) => Math.abs(toX - fromX) >= minPixels;
