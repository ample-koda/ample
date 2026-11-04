// Strum (PR11): staggers the starts of a group of notes that begin together (a chord) over a short span, following a curve, with a
// velocity fade along the strum. Pure functions, ticks in, ticks out; the caller writes the result back through the notes model.

export const STRUM_CURVES = ['even', 'slow-then-fast', 'fast-then-slow', 'guitar', 'harp'];
export const STRUM_DIRECTIONS = ['down', 'up', 'alternate'];

function curveT(curve, t) {
  switch (curve) {
    case 'slow-then-fast': return t * t;
    case 'fast-then-slow': return 1 - (1 - t) * (1 - t);
    case 'guitar': return t ** 1.6;
    case 'harp': return Math.sin((t * Math.PI) / 2);
    default: return t;   // even
  }
}

/** Splits notes (sorted by `at`, ascending) into chord groups: a new group starts whenever a note begins more than `toleranceTicks`
 * after the group's first note. */
export function groupNotesByStart(notes, toleranceTicks) {
  const sorted = [...notes].sort((a, b) => a.at - b.at);
  const groups = [];
  for (const n of sorted) {
    const last = groups.at(-1);
    if (last && n.at - last[0].at <= toleranceTicks) last.push(n); else groups.push([n]);
  }
  return groups;
}

/**
 * Strums one group of notes that start together: orders them by pitch (direction `down` = high to low, `up` = low to high, `alternate`
 * reverses the order compared to the previous call when `groupIndex` is odd), staggers the starts over `totalTicks` following `curve`,
 * and fades velocity toward the end of the strum by up to `accentPercent` (the first note keeps its own velocity). `keepEnds` extends or
 * shortens each note's length so it still ends where it originally did; otherwise the original length is kept. Each result keeps the
 * input note's other fields (notably `index`, from notes-model's `absolute()`), so the caller can write it back by index.
 */
export function strum(group, { direction = 'down', totalTicks = 240, curve = 'even', accentPercent = 20, keepEnds = true, groupIndex = 0 } = {}) {
  if (group.length < 2) return group;
  let ordered = [...group].sort((a, b) => a.pitch - b.pitch);
  const reversed = direction === 'down' || (direction === 'alternate' && groupIndex % 2 === 0);
  if (reversed) ordered = ordered.reverse();
  const n = ordered.length;
  return ordered.map((note, i) => {
    const t = curveT(curve, i / (n - 1));
    const at = note.at + Math.round(t * totalTicks);
    const end = note.at + note.length;
    const length = keepEnds ? Math.max(1, end - at) : note.length;
    const velocity = Math.max(1, Math.min(127, Math.round(note.velocity * (i === 0 ? 1 : 1 - (accentPercent / 100) * t))));
    return { ...note, at, length, velocity };
  });
}
