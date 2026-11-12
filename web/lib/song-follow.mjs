// Where a clip is while the song plays: the bar of the song that sounds now, turned into a position inside the clip (the Jam shows it as a second playhead).
// A clip is placed in the song in parts ({ at, length } in bars, 1-based); its notes start at the beginning of its first part, and a second part goes on where the first one ended.

/**
 * The position inside a clip, in ticks, for a bar of the song (fractional, 1-based). Null when the clip does not sound at that bar.
 * barTicks is the length of one bar in ticks; a clip without parts is there for the whole song from bar 1.
 */
export function clipTickAt(parts, bar, barTicks) {
  if (!Number.isFinite(bar) || !(barTicks > 0)) return null;
  if (!parts?.length) return bar >= 1 ? Math.round((bar - 1) * barTicks) : null;
  let before = 0;
  for (const p of [...parts].sort((x, y) => x.at - y.at)) {
    if (bar >= p.at && bar < p.at + p.length) return Math.round((before + bar - p.at) * barTicks);
    before += p.length;
  }
  return null;
}
