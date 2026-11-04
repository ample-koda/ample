// The neon lines of Koda (what Koda looks at, and what it glances at while it thinks): a line is a curve between Koda and a point whose middle hangs like a loose cord, on a spring, so it sways when either end
// moves. Pure. @ample:koda

/** A new line with its middle at the straight line. */
export const newStrand = (a, b) => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, vx: 0, vy: 0 });

/** One step of the spring (dt in ms): the middle moves toward a point below the straight line, by an amount that grows with the length. Returns s. */
export function stepStrand(s, a, b, dt, slack = 0.2) {
  const h = Math.min(0.05, dt / 1000);
  const len = Math.hypot(b.x - a.x, b.y - a.y);
  const tx = (a.x + b.x) / 2, ty = (a.y + b.y) / 2 + Math.min(90, len * slack);
  const K = 140, D = 9;
  s.vx += ((tx - s.x) * K - s.vx * D) * h;
  s.vy += ((ty - s.y) * K - s.vy * D) * h;
  s.x += s.vx * h; s.y += s.vy * h;
  return s;
}

/** The SVG path of the line: a quadratic curve whose control point is chosen so the curve passes through the swaying middle. */
export const strandPath = (a, b, s) => {
  const cx = 2 * s.x - (a.x + b.x) / 2, cy = 2 * s.y - (a.y + b.y) / 2;
  const r = (n) => Math.round(n * 10) / 10;
  return `M${r(a.x)} ${r(a.y)}Q${r(cx)} ${r(cy)} ${r(b.x)} ${r(b.y)}`;
};

/** Where a line ends on a rectangle: the middle of the side that faces the point it comes from (the centre when the point is inside), so a line to a row or a block ends level with its middle. */
export function nearestOnRect(rect, p) {
  const cx = (rect.left + rect.right) / 2, cy = (rect.top + rect.bottom) / 2;
  const w = Math.max(1, rect.right - rect.left), h = Math.max(1, rect.bottom - rect.top);
  const inside = p.x > rect.left && p.x < rect.right && p.y > rect.top && p.y < rect.bottom;
  if (inside) return { x: cx, y: cy };
  // a long thin box (a row of a list): the end is level with its middle on the side that faces the point, never on its top or bottom edge, which is the line between two rows
  if (w > 6 * h) return { x: p.x > cx ? rect.right : rect.left, y: cy };
  const dx = (p.x - cx) / w, dy = (p.y - cy) / h;
  if (Math.abs(dx) >= Math.abs(dy)) return { x: dx > 0 ? rect.right : rect.left, y: cy };
  return { x: cx, y: dy > 0 ? rect.bottom : rect.top };
}
