// The placements ("parts") of a clip in the song after the clip is made longer. Pure (no DOM, no store), so it is tested in node.
// Keeping an extended Jam take used to write a list with only the first placement, which erased every other place where the clip is used.

/**
 * Makes the placement that the editor shows (the first part) `bars` bars long and keeps every other placement as it is.
 * A part of one block may not overlap the next one: when the longer clip would run into the nearest later placement
 * it stops just before it.
 * @param {{ at: number, length: number, name?: string }[] | undefined} parts the parts of the block now
 * @param {number} bars the wanted length in bars
 * @returns {{ parts: { at: number, length: number, name: string }[], length: number, clamped: boolean }}
 */
export function extendedParts(parts, bars) {
  const list = Array.isArray(parts) ? parts.filter((p) => p && Number.isFinite(p.at) && Number.isFinite(p.length)) : [];
  const wanted = Math.max(1, Math.ceil(Number(bars) || 1));
  if (!list.length) return { parts: [{ at: 1, length: wanted, name: '' }], length: wanted, clamped: false };
  const first = list[0];
  const later = list.slice(1).filter((p) => p.at > first.at).map((p) => p.at - first.at);
  const room = later.length ? Math.min(...later) : Infinity;
  const length = Math.max(first.length, Math.min(wanted, room));
  const out = list.map((p, i) => ({ at: p.at, length: i === 0 ? length : p.length, name: p.name || '' }));
  return { parts: out, length, clamped: length < wanted };
}
