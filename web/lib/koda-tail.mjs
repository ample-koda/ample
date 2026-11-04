// The tail of Koda: a ribbon that hangs from the top of the stem. Every piece pulls back to a shape (a curve that hangs down), the movement of the body and of the page push it, and a wave runs through it.
// No DOM here: a step takes the point where the tail is fixed and gives the outline of the ribbon as a path. @ample:koda

export const SEGMENTS = 11;
export const LENGTH = 7.6;

/** The hanging shape of each emotion: a0 is the first angle in degrees (0 to the right, positive down), bend how much each piece turns further down, stiff how hard it pulls back, amp, freq and phase the wave. */
export const TAILS = {
  neutral:   { a0: 5,   bend: 12,  stiff: 0.16, amp: 0.10, freq: 1.6, phase: 0.5 },
  happy:     { a0: -10, bend: 10,  stiff: 0.14, amp: 0.28, freq: 5,   phase: 0.6 },
  excited:   { a0: -12, bend: 12,  stiff: 0.12, amp: 0.5,  freq: 12,  phase: 0.5 },
  love:      { a0: 0,   bend: 14,  stiff: 0.10, amp: 0.34, freq: 2.6, phase: 0.7 },
  surprised: { a0: -38, bend: 9,   stiff: 0.30, amp: 0.12, freq: 18,  phase: 0.2 },
  confused:  { a0: -20, bend: -14, stiff: 0.10, amp: 0.30, freq: 2.2, phase: 1.6 },
  sad:       { a0: 70,  bend: 4,   stiff: 0.06, amp: 0.04, freq: 1,   phase: 0.4 },
  sleepy:    { a0: 55,  bend: 6,   stiff: 0.05, amp: 0.07, freq: 0.9, phase: 0.4 },
  angry:     { a0: -34, bend: 8,   stiff: 0.34, amp: 0.04, freq: 9,   phase: 0.3 },
  shy:       { a0: 25,  bend: 24,  stiff: 0.09, amp: 0.08, freq: 1.4, phase: 0.5 },
  think:     { a0: 8,   bend: 12,  stiff: 0.12, amp: 0.22, freq: 2.2, phase: 0.6 },
  asking:    { a0: -25, bend: 18,  stiff: 0.10, amp: 0.20, freq: 2.4, phase: 0.8 },
  talk:      { a0: 5,   bend: 12,  stiff: 0.12, amp: 0.30, freq: 7,   phase: 0.6 },
  jump:      { a0: -12, bend: 12,  stiff: 0.12, amp: 0.50, freq: 12,  phase: 0.5 },
};

/** A new tail hanging from (x, y). */
export const createTail = (x = 0, y = 0) => ({ pts: Array.from({ length: SEGMENTS + 1 }, (_, k) => ({ x: x + k * LENGTH, y: y + k * 2, vx: 0, vy: 0 })) });

/**
 * One step: the base is where the tail is fixed (bx, by), rot the turn of the body in radians, t the time in ms, (dx, dy) how far the whole picture moved since the last step (the pieces stay behind),
 * cfg one of TAILS, still true for reduced motion (no wave). Returns the path of the outline.
 */
export function stepTail(tail, { bx, by, rot = 0, t = 0, dx = 0, dy = 0, cfg = TAILS.neutral, still = false }) {
  const pts = tail.pts;
  if (dx || dy) for (const p of pts) { p.x -= dx; p.y -= dy; }
  pts[0].x = bx; pts[0].y = by;
  let angle = rot + (cfg.a0 * Math.PI) / 180;
  const wave = still ? 0 : 1;
  for (let i = 1; i <= SEGMENTS; i++) {
    angle += ((cfg.bend * Math.PI) / 180) * (i === 1 ? 0.4 : 1) * 0.55;
    const shake = wave * cfg.amp * Math.sin((t / 1000) * cfg.freq - i * cfg.phase) * (i / SEGMENTS + 0.25);
    const tx = pts[i - 1].x + LENGTH * Math.cos(angle + shake);
    const ty = pts[i - 1].y + LENGTH * Math.sin(angle + shake);
    const p = pts[i];
    p.vx = (p.vx + (tx - p.x) * cfg.stiff) * 0.82;
    p.vy = (p.vy + (ty - p.y) * cfg.stiff + 0.06) * 0.82;
    p.x += p.vx; p.y += p.vy;
    // keep the pieces at their length so it stays one ribbon
    const ex = p.x - pts[i - 1].x;
    const ey = p.y - pts[i - 1].y;
    const len = Math.hypot(ex, ey) || 1;
    p.x = pts[i - 1].x + (ex / len) * LENGTH; p.y = pts[i - 1].y + (ey / len) * LENGTH;
  }
  return outline(pts);
}

/** A ribbon that gets thinner towards its tip, around the line of points. */
export function outline(pts) {
  const left = [];
  const right = [];
  const n = pts.length - 1;
  for (let i = 0; i <= n; i++) {
    const p = pts[i], q = pts[Math.min(n, i + 1)], o = pts[Math.max(0, i - 1)];
    let nx = -(q.y - o.y), ny = q.x - o.x;
    const nl = Math.hypot(nx, ny) || 1;
    nx /= nl; ny /= nl;
    const wd = 4.6 * (1 - i / (n + 1)) + 0.8;
    left.push([p.x + nx * wd, p.y + ny * wd]); right.push([p.x - nx * wd, p.y - ny * wd]);
  }
  const f = (p) => `${p[0].toFixed(1)} ${p[1].toFixed(1)}`;
  const tip = pts[n];
  return `M${left.map(f).join('L')}L${tip.x.toFixed(1)} ${tip.y.toFixed(1)}L${right.reverse().map(f).join('L')}Z`;
}
