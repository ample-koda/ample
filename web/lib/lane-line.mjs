/** Values for notes whose starts lie between the two endpoints of a straight property-lane gesture. */
export function laneLineChanges(notes, startAt, endAt, startValue, endValue, key, min, max) {
  const changes = new Map();
  const lo = Math.min(startAt, endAt), hi = Math.max(startAt, endAt);
  for (const note of notes) {
    if (note.at < lo || note.at > hi) continue;
    const ratio = startAt === endAt ? 0 : (note.at - startAt) / (endAt - startAt);
    changes.set(note.index, { [key]: Math.max(min, Math.min(max, Math.round(startValue + (endValue - startValue) * ratio))) });
  }
  return changes;
}
