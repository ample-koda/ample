// Pure maths for touch gestures on the canvas (mobile-ui plan, MUI03). No DOM here, so it can be tested with node.
// @ample:mobile-ui

export const LONG_PRESS_MS = 500;
export const SLOP = 10;

/** True while the finger stayed within the slop radius of where it went down (so it is still a press, not a drag). */
export function withinSlop(start, now, slop = SLOP) {
  return Math.hypot(now.x - start.x, now.y - start.y) <= slop;
}

/** A long press fires when the finger has been down long enough without leaving the slop radius. */
export function isLongPress({ start, now, downAt, at }, ms = LONG_PRESS_MS, slop = SLOP) {
  return at - downAt >= ms && withinSlop(start, now, slop);
}

/**
 * The tap-tap connect mode as a small state machine.
 * state: null (nothing chosen) or { node, idx } (an output dot is chosen).
 * tap: { dir: 'out' | 'in' | null, node, idx } (dir null = a tap somewhere else).
 * Returns { state, action }: action is 'choose', 'cancel', 'connect' (with from/to), 'need-output' or 'none'.
 */
export function tapConnect(state, tap) {
  if (tap.dir === 'out') {
    if (state && state.node === tap.node && state.idx === tap.idx) return { state: null, action: 'cancel' };
    return { state: { node: tap.node, idx: tap.idx }, action: 'choose' };
  }
  if (tap.dir === 'in') {
    if (!state) return { state: null, action: 'need-output' };
    return { state: null, action: 'connect', from: state, to: { node: tap.node, idx: tap.idx } };
  }
  return state ? { state: null, action: 'cancel' } : { state: null, action: 'none' };
}

/**
 * Chromium moves a touch tap to the nearest clickable element ("touch adjustment"). At a small zoom a connector dot and the small buttons of a block
 * (</>, arrow, pin) are a few pixels apart, so a tap meant for a dot could press a button (the </> button switches the whole canvas to the source view).
 * `point` is where the finger really went down; `buttonRect` the pressed button; `ports` the rects of the dots. Returns the index of the dot the finger
 * was on (within `reachX`/`reachY` px of it, and nearer to it than to the button), or -1 when the button was meant.
 */
export function redirectToPort(point, buttonRect, ports, reachX = 12, reachY = 6) {
  const inside = (r, ax, ay) => point.x >= r.left - ax && point.x <= r.right + ax && point.y >= r.top - ay && point.y <= r.bottom + ay;
  const dist = (r) => Math.hypot(point.x - (r.left + r.right) / 2, point.y - (r.top + r.bottom) / 2);
  let best = -1, bestDistance = Infinity;
  ports.forEach((r, i) => { if (inside(r, reachX, reachY) && dist(r) < bestDistance) { best = i; bestDistance = dist(r); } });
  if (best < 0) return -1;
  if (inside(buttonRect, 0, 0) && dist(buttonRect) <= bestDistance) return -1;
  return best;
}
