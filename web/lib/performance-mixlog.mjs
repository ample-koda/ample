// What the DJ did with the faders during a set (phase 5): the mix of the lanes is logged as it changes and, when the set is saved as a song, turned into automation curves, auto(bar:value, ...),
// for the volume, the pan and the filter of every lane. Pure, so node --test checks it.

/** The log of the mix: a snapshot of what every lane sounds like (gain 0..1, pan -1..1, cutoff in Hz or null) at a beat of the set, written only when something changed. */
export function createMixLog() {
  const snaps = [];
  return {
    record(beat, settings) {
      const lanes = {};
      for (const [lane, s] of settings) lanes[lane] = { gain: s.gain, pan: s.pan, cutoff: s.cutoff ?? null };
      const key = JSON.stringify(lanes);
      if (snaps.length && snaps.at(-1).key === key) return false;
      snaps.push({ beat, lanes, key });
      return true;
    },
    reset() { snaps.length = 0; },
    snapshots: () => snaps.map(({ beat, lanes }) => ({ beat, lanes })),
  };
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
export const OPEN = 20000;   // a filter that is open: the top of the range

/** The values of a lane in the units of the blocks of the saved song: the gain in dB (-60 is silence), the pan from -32 to 32 and the cutoff in Hz. */
export const unitsOf = (lane) => ({
  gain: lane.gain <= 0.001 ? -60 : clamp(20 * Math.log10(lane.gain), -60, 12),
  pan: clamp(Math.round(lane.pan * 32), -32, 32),
  cutoff: lane.cutoff === null || lane.cutoff === undefined ? OPEN : clamp(lane.cutoff, 20, OPEN),
});

/** Ramer-Douglas-Peucker on a curve of [x, y] points: the points that are needed to stay within `eps` of the curve (on y). */
function simplify(points, eps) {
  if (points.length < 3) return points;
  const [x0, y0] = points[0], [x1, y1] = points.at(-1);
  let worst = -1, at = 0;
  for (let i = 1; i < points.length - 1; i++) {
    const t = x1 === x0 ? 0 : (points[i][0] - x0) / (x1 - x0);
    const d = Math.abs(points[i][1] - (y0 + (y1 - y0) * t));
    if (d > worst) { worst = d; at = i; }
  }
  if (worst <= eps) return [points[0], points.at(-1)];
  return [...simplify(points.slice(0, at + 1), eps).slice(0, -1), ...simplify(points.slice(at), eps)];
}

/**
 * The curves of one lane: { gain, pan, cutoff }, each { value } when it did not move (value is its constant) or { points: [[bar, value], ...] } when it did. Bars are counted from 1 in the saved song:
 * `shiftBeats` is the beat of the first launch (bar 1), a snapshot from before it gives the value at bar 1. A jump (a mute) is a step: the old value is held until just before the new one.
 */
export function laneCurves(snapshots, lane, { shiftBeats = 0, beatsPerBar = 4 } = {}) {
  const series = { gain: [], pan: [], cutoff: [] };
  let initial = null;
  for (const snap of snapshots) {
    const values = snap.lanes[lane] ? unitsOf(snap.lanes[lane]) : null;
    if (!values) continue;
    const bar = 1 + (snap.beat - shiftBeats) / beatsPerBar;
    if (bar <= 1) { initial = values; continue; }
    if (!initial) initial = { gain: 0, pan: 0, cutoff: OPEN };
    for (const key of Object.keys(series)) {
      const list = series[key];
      if (!list.length) list.push([1, initial[key]]);
      const last = list.at(-1);
      if (last[1] === values[key]) continue;
      // a value that was held for a while (half a bar or more) and then jumps: hold it up to just before the jump; a drag, which changes every few milliseconds, is followed as it is
      if (bar - last[0] > 0.5) list.push([Math.round((bar - 0.04) * 1000) / 1000, last[1]]);
      list.push([Math.round(bar * 1000) / 1000, values[key]]);
    }
  }

  const start = initial ?? { gain: 0, pan: 0, cutoff: OPEN };
  const out = {};
  for (const key of Object.keys(series)) {
    const list = series[key];
    if (!list.length) { out[key] = { value: key === 'gain' ? Math.round(start[key] * 10) / 10 : Math.round(start[key]) }; continue; }
    // the log domain for the cutoff (an octave is the same distance anywhere), dB and the pan steps as they are
    const to = key === 'cutoff' ? (v) => Math.log2(v) : (v) => v;
    const from = key === 'cutoff' ? (v) => 2 ** v : (v) => v;
    const eps = key === 'gain' ? 0.25 : key === 'pan' ? 0.5 : 0.04;
    const thin = simplify(list.map(([x, y]) => [x, to(y)]), eps).map(([x, y]) => [x, from(y)]);
    const round = key === 'gain' ? (v) => Math.round(v * 10) / 10 : (v) => Math.round(v);
    const points = thin.map(([x, y]) => [x, round(y)]);
    out[key] = points.every(([, y]) => y === points[0][1]) ? { value: points[0][1] } : { points };
  }

  return out;
}

/** auto(1:0, 2.5:-6): the text of a curve in a song. */
export const autoText = (points) => `auto(${points.map(([bar, value]) => `${bar}:${value}`).join(', ')})`;

/** Whether the mix of a lane did anything: a curve that moved, or a value that is not the neutral one. */
export const moved = (curves) => (curves.gain.points || curves.pan.points || curves.cutoff.points)
  ? true : (curves.gain.value !== 0 || curves.pan.value !== 0 || curves.cutoff.value !== OPEN);
