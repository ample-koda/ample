// Touch maths of the Lite piano roll (mobile-ui MUI06): two-finger pinch zoom (time and rows) with two-finger pan, and the long press.
// Pure functions, no DOM. Points are { x, y } in grid pixels. A "view" is { pxBeat, rowH, scrollX, scrollY }; in the Grid view the row
// size and the vertical scroll are called gridRowH and gridScrollY, selected through `keys`.
import { LIMITS } from './lite-flow.mjs';
import { LONG_PRESS_MS, isLongPress } from './touch-gestures.mjs';

export { LONG_PRESS_MS, isLongPress };
export const ROLL_KEYS = { row: 'rowH', scroll: 'scrollY', minRow: LIMITS.minRowH, maxRow: LIMITS.maxRowH };
export const GRID_KEYS = { row: 'gridRowH', scroll: 'gridScrollY', minRow: LIMITS.minGridRowH, maxRow: LIMITS.maxGridRowH };
/** Fingers closer than this on an axis do not zoom that axis (aligned fingers zoom only the other one); when both axes are that small the whole distance zooms both. */
export const MIN_AXIS_PX = 36;

const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

/** The two fingers as a start record: midpoint, distance and the distance on each axis. */
export function pinchStart(a, b) {
  const dx = Math.abs(b.x - a.x), dy = Math.abs(b.y - a.y);
  return { mid: { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 }, dx, dy, dist: Math.hypot(dx, dy) };
}

/** The zoom factor of one axis: the change of the finger distance on that axis; 1 when the fingers are aligned on the other axis. */
export function axisFactor(startAxis, nowAxis, startDist, nowDist) {
  const f = startAxis >= MIN_AXIS_PX ? nowAxis / startAxis : startDist > 0 && startDist < MIN_AXIS_PX * 1.5 ? nowDist / startDist : 1;
  return clamp(Number.isFinite(f) && f > 0 ? f : 1, 0.2, 5);
}

/**
 * The view for the fingers now (`now`), given the view when the pinch started (`view0`) and the start record: the beat and the row that were
 * under the start midpoint stay under the current midpoint, so one move does zoom and pan. `max` ({ x, y }) caps the scroll when known.
 */
export function pinchView(view0, start, now, keys = ROLL_KEYS, max = {}) {
  const fx = axisFactor(start.dx, now.dx, start.dist, now.dist), fy = axisFactor(start.dy, now.dy, start.dist, now.dist);
  const next = { ...view0 };
  const beat = (start.mid.x + view0.scrollX) / view0.pxBeat;
  next.pxBeat = clamp(view0.pxBeat * fx, LIMITS.minPxBeat, LIMITS.maxPxBeat);
  next.scrollX = clamp(beat * next.pxBeat - now.mid.x, 0, max.x ?? Infinity);
  const row = (start.mid.y + view0[keys.scroll]) / view0[keys.row];
  next[keys.row] = clamp(view0[keys.row] * fy, keys.minRow, keys.maxRow);
  next[keys.scroll] = clamp(row * next[keys.row] - now.mid.y, 0, max.y ?? Infinity);
  return next;
}

/** What a long press does at a point: delete the note under the finger, the grid's accent on a step, nothing (undo the draw) on empty ground. */
export function longPressAction({ mode, hitNote }) {
  if (mode === 'grid') return 'accent';
  return hitNote ? 'delete' : 'cancel';
}
