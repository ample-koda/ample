// The loop range of the live chain (local-preview chunk 5.5): N bars of the take instead of the whole thing. Pure maths on seconds.

/**
 * The region of `bars` bars that contains `position` (seconds into the take): { start, length, anchor, at } where `anchor`/`at` say the playhead was at `position` at audio time `now`.
 * The start snaps down to a bar line; a region that would run past the end of the take starts earlier so it still fits.
 */
export function regionAt(position, barSeconds, bars, takeSeconds, now = 0) {
  const length = Math.min(bars * barSeconds, takeSeconds);
  let start = Math.floor(position / barSeconds) * barSeconds;
  if (start + length > takeSeconds) start = Math.max(0, Math.floor((takeSeconds - length) / barSeconds + 1e-9) * barSeconds);
  return { start, length, anchor: position, at: now };
}

/** Where the loop is at audio time `now`. Without a region: the whole take loops from `t0`. */
export function positionIn(region, now, t0, takeSeconds) {
  if (!region) return takeSeconds > 0 ? (((now - t0) % takeSeconds) + takeSeconds) % takeSeconds : 0;
  const offset = (region.anchor - region.start) + (now - region.at);
  return region.start + (((offset % region.length) + region.length) % region.length);
}
