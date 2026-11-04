// The lower lane of the roll: one value per note (velocity, pan, fine pitch, release, cutoff), drawn as a stem and edited by dragging. Pure. @ample:pianoroll-lite

/** What each lane edits: the note field, its range, whether 0 is the middle (a bipolar value), what an untouched note shows and the short label. */
export const LANE_DEFS = {
  velocity: { key: 'velocity', min: 1, max: 127, mid: false, reset: 100, label: 'Velocity', short: 'vel' },
  pan: { key: 'pan', min: -100, max: 100, mid: true, reset: 0, label: 'Pan', short: 'pan' },
  fine: { key: 'fine', min: -100, max: 100, mid: true, reset: 0, label: 'Fine pitch (cents)', short: 'fine' },
  release: { key: 'rel', min: 0, max: 5000, mid: false, reset: undefined, label: 'Release (ms)', short: 'rel' },
  cutoff: { key: 'cut', min: 0, max: 100, mid: false, reset: 100, label: 'Cutoff', short: 'cut' },
};

/** The value a note shows in a lane: its own, or the lane's default when the note has none. */
export const laneValueOf = (note, def) => { const v = note[def.key]; return v === undefined ? (def.reset ?? 0) : v; };

/** A value kept inside the lane's range, whole numbers only. */
export const laneClamp = (v, def) => Math.max(def.min, Math.min(def.max, Math.round(v)));

/** The value under a height in the lane (y counted from the top of the drawing area of height `height`). */
export const laneFromY = (y, height, def) => laneClamp(def.min + (1 - Math.max(0, Math.min(1, y / Math.max(1, height)))) * (def.max - def.min), def);

/** The fraction 0..1 of the lane's range that a value fills (the stem height for a one-sided lane, the distance from the middle line for a bipolar one). */
export const laneFraction = (v, def) => (Math.max(def.min, Math.min(def.max, v)) - def.min) / (def.max - def.min);

/** Values along a ramp from (tickA, valueA) to (tickB, valueB) for every row that starts between them: Map(index -> value). */
export function laneRamp(rows, tickA, valueA, tickB, valueB, def) {
  const [t0, v0, t1, v1] = tickA <= tickB ? [tickA, valueA, tickB, valueB] : [tickB, valueB, tickA, valueA];
  const out = new Map();
  for (const row of rows) {
    if (row.at < t0 || row.at > t1) continue;
    const f = t1 === t0 ? 1 : (row.at - t0) / (t1 - t0);
    out.set(row.index, laneClamp(v0 + (v1 - v0) * f, def));
  }
  return out;
}
