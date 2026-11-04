// Auto formatting of the blocks on the canvas (pure, tested without a DOM): the selected blocks are put in columns by the way the signal flows (what feeds a
// block is left of it), the rows of a column follow the rows of what feeds them so the links cross less, and blocks that are not linked to the others of the
// selection go in a grid below. The group keeps the top left corner it had. Blocks that are not selected never move. @ample:auto-arrange

const DEFAULT_COLUMN_GAP = 70, DEFAULT_ROW_GAP = 30, GRID_COLUMNS = 4, SNAP = 10, WRAP_WIDTH = 1500, BAND_GAP = 70;
const snap = (v) => Math.round(v / SNAP) * SNAP;

/**
 * @param {{ id: string, x: number, y: number, w: number, h: number, last?: boolean }[]} nodes the blocks of the canvas, with the size they take; a block marked `last` (the master) always ends in the last column
 * @param {{ from: string, to: string }[]} links
 * @param {Set<string> | string[]} selected the ids to arrange
 * @param {Set<string> | string[]} [pinned] blocks that stay where they are (selected or not): they are not moved, and the blocks that are arranged are placed around them
 * @param {{ mode?: 'ltr' | 'ttb' | 'wrap', columnGap?: number, rowGap?: number }} [options] the layout: `ltr` (default) the signal flows left to right; `ttb` it flows top to bottom; `wrap` it flows left to right and continues in a new band below when the row gets wider than one screen
 * @returns {Map<string, { x: number, y: number }>} the new place of every arranged block
 */
export function autoArrange(nodes, links, selected, pinned = [], options = {}) {
  if (options.mode === 'ttb') {
    // the same layout with the axes swapped: the sizes and places are turned, arranged left to right, and turned back
    const turned = nodes.map((n) => ({ ...n, x: n.y, y: n.x, w: n.h, h: n.w }));
    const out = autoArrange(turned, links, selected, pinned, { mode: 'ltr', columnGap: 50, rowGap: 40 });
    return new Map([...out].map(([id, p]) => [id, { x: p.y, y: p.x }]));
  }
  const COLUMN_GAP = options.columnGap ?? DEFAULT_COLUMN_GAP, ROW_GAP = options.rowGap ?? DEFAULT_ROW_GAP;
  const fixed = new Set(pinned);
  const ids = new Set(selected);
  const sel = nodes.filter((n) => ids.has(n.id) && !fixed.has(n.id));
  const result = new Map();
  if (sel.length < 2 && !(sel.length === 1 && fixed.size)) return result;
  const byId = new Map(sel.map((n) => [n.id, n]));
  const edges = links.filter((l) => byId.has(l.from) && byId.has(l.to) && l.from !== l.to);
  const parents = new Map(sel.map((n) => [n.id, []]));
  for (const e of edges) parents.get(e.to).push(e.from);

  // the column of a block: one further than the furthest block that feeds it (the passes stop on a loop)
  const depth = new Map(sel.map((n) => [n.id, 0]));
  for (let pass = 0; pass < sel.length; pass++) {
    let changed = false;
    for (const e of edges) if (depth.get(e.to) < depth.get(e.from) + 1 && depth.get(e.from) + 1 < sel.length) { depth.set(e.to, depth.get(e.from) + 1); changed = true; }
    if (!changed) break;
  }
  // the master (marked last) ends at the right, whatever its path is
  const furthest = Math.max(0, ...[...depth.values()]);
  for (const n of sel) if (n.last && edges.some((e) => e.to === n.id || e.from === n.id)) depth.set(n.id, Math.max(depth.get(n.id), furthest));
  const linked = new Set(edges.flatMap((e) => [e.from, e.to]));
  const chain = sel.filter((n) => linked.has(n.id));
  const loose = sel.filter((n) => !linked.has(n.id)).sort((a, b) => a.y - b.y || a.x - b.x);

  const x0 = Math.min(...sel.map((n) => n.x)), y0 = Math.min(...sel.map((n) => n.y));
  let used = 0;
  if (chain.length) {
    const columns = [];
    for (const n of chain) (columns[depth.get(n.id)] ||= []).push(n);
    const order = new Map();
    columns.forEach((column) => { column?.sort((a, b) => a.y - b.y || a.x - b.x); column?.forEach((n, i) => order.set(n.id, i)); });
    // several sweeps, down (by the blocks that feed a block) and up (by the blocks it feeds): a block goes to the average row of its neighbours, which takes
    // most of the crossings out of the links
    const children = new Map(sel.map((n) => [n.id, []]));
    for (const e of edges) children.get(e.from).push(e.to);
    const sweep = (column, neighbours) => {
      const score = (n) => { const p = neighbours.get(n.id).filter((id) => order.has(id)); return p.length ? p.reduce((sum, id) => sum + order.get(id), 0) / p.length : order.get(n.id); };
      column.sort((x, y) => score(x) - score(y) || order.get(x.id) - order.get(y.id));
      column.forEach((n, i) => order.set(n.id, i));
    };
    for (let round = 0; round < 4; round++) {
      for (let c = 1; c < columns.length; c++) if (columns[c]) sweep(columns[c], parents);
      for (let c = columns.length - 2; c >= 0; c--) if (columns[c]) sweep(columns[c], children);
    }
    const heights = columns.map((column) => (column ? column.reduce((sum, n) => sum + n.h, 0) + (column.length - 1) * ROW_GAP : 0));
    const tallest = Math.max(...heights);
    let x = x0;
    columns.forEach((column, c) => {
      if (!column) return;
      let y = y0 + (tallest - heights[c]) / 2;   // a short column is centred against the tallest one
      for (const n of column) { result.set(n.id, { x: snap(x), y: snap(y) }); y += n.h + ROW_GAP; }
      x += Math.max(...column.map((n) => n.w)) + COLUMN_GAP;
    });
    // a link that jumps over columns (the dry sound past an effect chain) would run straight through the blocks in between: those columns go below both ends of it,
    // so the link has a lane of its own above them
    columns.forEach((column, c) => {
      if (!column) return;
      let needed = -Infinity;
      for (const e of edges) {
        const from = depth.get(e.from), to = depth.get(e.to);
        if (from < c && c < to) needed = Math.max(needed, ...[e.from, e.to].map((id) => result.get(id).y + byId.get(id).h + ROW_GAP));
      }
      const top = Math.min(...column.map((n) => result.get(n.id).y));
      if (needed > top) for (const n of column) result.set(n.id, { x: result.get(n.id).x, y: snap(result.get(n.id).y + needed - top) });
    });
    if (options.mode === 'wrap') wrapBands(result, byId, x0, y0);
    used = Math.max(tallest, ...[...result.entries()].map(([id, at]) => at.y + byId.get(id).h - y0)) + ROW_GAP * 2;
  }
  // blocks without a link to the others: a grid below
  let x = x0, y = y0 + used, rowHeight = 0;
  loose.forEach((n, i) => {
    if (i > 0 && i % GRID_COLUMNS === 0) { x = x0; y += rowHeight + ROW_GAP; rowHeight = 0; }
    result.set(n.id, { x: snap(x), y: snap(y) });
    x += n.w + COLUMN_GAP / 2; rowHeight = Math.max(rowHeight, n.h);
  });
  return clearOf(result, nodes.filter((n) => fixed.has(n.id)), nodes);
}

/** Folds the columns into bands: a column that would make the row wider than one screen starts a new band below the previous one (the links between the bands are long, the whole is compact). */
function wrapBands(result, byId, x0, y0) {
  const columns = new Map();
  for (const [id, at] of result) { if (!columns.has(at.x)) columns.set(at.x, []); columns.get(at.x).push(id); }
  const xs = [...columns.keys()].sort((a, b) => a - b);
  let bandX = xs[0], dx = 0, dy = 0, bottom = -Infinity, bandBottom = -Infinity;
  for (const x of xs) {
    const width = Math.max(...columns.get(x).map((id) => byId.get(id).w));
    if (x + width - bandX > WRAP_WIDTH && x !== bandX) { bandX = x; dx = x0 - x; dy = bottom + BAND_GAP - y0; bandBottom = -Infinity; }
    for (const id of columns.get(x)) {
      const at = result.get(id);
      result.set(id, { x: snap(at.x + dx), y: snap(at.y + dy) });
      bandBottom = Math.max(bandBottom, at.y + dy + byId.get(id).h);
    }
    bottom = Math.max(bottom, bandBottom);
  }
}

const MARGIN = 20;
const overlaps = (a, b) => a.x < b.x + b.w + MARGIN && b.x < a.x + a.w + MARGIN && a.y < b.y + b.h + MARGIN && b.y < a.y + a.h + MARGIN;

/** Moves the arranged blocks down, one by one, past the pinned blocks and the blocks that were placed before them, so nothing lies on top of anything. */
function clearOf(result, pinned, nodes) {
  const size = new Map(nodes.map((n) => [n.id, n]));
  const obstacles = pinned.map((n) => ({ x: n.x, y: n.y, w: n.w, h: n.h }));
  const order = [...result.entries()].sort((a, b) => a[1].x - b[1].x || a[1].y - b[1].y);
  for (const [id, at] of order) {
    const node = size.get(id);
    let y = at.y;
    for (let guard = 0; guard < 60; guard++) {
      const hit = obstacles.find((o) => overlaps({ x: at.x, y, w: node.w, h: node.h }, o));
      if (!hit) break;
      y = Math.ceil((hit.y + hit.h + MARGIN) / SNAP) * SNAP;
    }
    result.set(id, { x: at.x, y });
    obstacles.push({ x: at.x, y, w: node.w, h: node.h });
  }
  return result;
}
