// Is the pointer being shaken? A quick back and forth (at least four turns within 700 ms, each of some distance) on either axis. Pure. @ample:koda

const TURN = 14;      // how far the pointer must come back before it counts as a turn (px)
const NEED = 4;       // turns that make a shake (the default)
const WINDOW = 700;   // within this time (ms)

/** feed(x, y, t) for every pointer move; it answers true as soon as the pointer has been shaken. reset() starts over. */
export function makeShake(need = NEED) {
  const axis = () => ({ dir: 0, ext: 0, turns: [] });
  let ax = axis(), ay = axis();
  const step = (a, v, t) => {
    if (a.dir === 0) { a.ext = v; a.dir = 1; return; }
    if ((v - a.ext) * a.dir > 0) { a.ext = v; return; }                       // still going the same way
    if (Math.abs(v - a.ext) >= TURN) { a.dir = -a.dir; a.ext = v; a.turns.push(t); }   // came back far enough: a turn
    a.turns = a.turns.filter((x) => t - x <= WINDOW);
  };
  return {
    feed(x, y, t) { step(ax, x, t); step(ay, y, t); return ax.turns.length >= need || ay.turns.length >= need; },
    reset() { ax = axis(); ay = axis(); },
  };
}
