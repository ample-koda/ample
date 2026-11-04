// Pure geometry and layout maths for the canvas. No DOM here, so node --test can check it.
// @ample:canvas

export const W = 300;
export const HEAD = 60;
export const ROW = 20;
export const GRID = 10;
export const MIN_ZOOM = 0.2;
export const MAX_ZOOM = 2.5;

export const snap = (v, grid = GRID) => Math.round(v / grid) * grid;
export const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

const labelWidth = (label) => String(label || '').length * 7;
/** How much a block is scaled on the canvas by its size grip (1 is the normal size); kept per block id by the canvas. */
export const sizes = {};
export const scaleOf = (node) => sizes[node.id] || 1;

/** The width of a block at normal size. */
export function baseWidth(node) {
  if (node.tpl === 'volume') return 260;
  const inputWidth = Math.max(0, ...(node.ins || []).map((port) => labelWidth(port[0])));
  const outputWidth = Math.max(0, ...(node.outs || []).map((port) => labelWidth(port[0])));
  const titleWidth = node.proxy ? 20 : 280;
  return clamp(Math.max(W, inputWidth + outputWidth + 56, inputWidth * 2 + 56, outputWidth * 2 + 56, titleWidth), W, 680);
}

/** The width a block takes on the canvas. */
export const nodeWidth = (node) => baseWidth(node) * scaleOf(node);

/** Render recipes only connect to render; other types connect when equal or either side is "any". */
export const compatible = (a, b) => controlsFit(a, b) || a === b || a !== 'render' && b !== 'render' && a !== 'region' && b !== 'region' && (a === 'any' || b === 'any');

/** World position of a port dot. */
export const portPos = (n, dir, i) => ({ x: n.x + (dir === 'out' ? nodeWidth(n) : 0), y: n.y + (HEAD + i * ROW + ROW / 2) * scaleOf(n) });

/** The bezier of a link, as an SVG path. */
export function linkPath(a, b) {
  const dx = Math.max(40, Math.abs(b.x - a.x) / 2);
  return `M${a.x} ${a.y} C ${a.x + dx} ${a.y} ${b.x - dx} ${b.y} ${b.x} ${b.y}`;
}

/** Screen position (client coordinates) to world position. */
export const toWorld = (view, rect, cx, cy) => ({ x: (cx - rect.left - view.x) / view.k, y: (cy - rect.top - view.y) / view.k });

/** Zooms about a point of the canvas (in canvas pixels), keeping that point still. */
export function zoomAt(view, mx, my, factor) {
  const k1 = clamp(view.k * factor, MIN_ZOOM, MAX_ZOOM);
  return { k: k1, x: mx - (mx - view.x) * (k1 / view.k), y: my - (my - view.y) * (k1 / view.k) };
}

/** Height of a block box (header, port rows, footer). */
export const nodeHeight = (n) => (HEAD + Math.max((n.ins || []).length, (n.outs || []).length, 1) * ROW + (n.tpl === 'volume' ? 110 : n.effectChain?.length || (n.outs || []).some((port) => port[1] === 'audio') ? 78 : 56) + (n.cardHeight || 0)) * scaleOf(n);

export function bounds(nodes) {
  if (!nodes.length) return null;
  return {
    x0: Math.min(...nodes.map((n) => n.x)),
    y0: Math.min(...nodes.map((n) => n.y)),
    x1: Math.max(...nodes.map((n) => n.x + nodeWidth(n))),
    y1: Math.max(...nodes.map((n) => n.y + nodeHeight(n))),
  };
}

/** A view that shows all nodes in a canvas of the given size; top is a band at the top that stays free (the view switcher floats there). */
export function fitView(nodes, size, margin = 60, top = 0) {
  const b = bounds(nodes);
  if (!b) return { x: 20, y: 10, k: 0.8 };
  const height = size.height - top;
  const k = clamp(Math.min((size.width - margin) / (b.x1 - b.x0), (height - margin) / (b.y1 - b.y0)), MIN_ZOOM, 1.5);
  return { k, x: (size.width - (b.x1 - b.x0) * k) / 2 - b.x0 * k, y: top + (height - (b.y1 - b.y0) * k) / 2 - b.y0 * k };
}

/**
 * The view for a narrow screen (a phone): the whole graph at once would be too small to read, so below half the view stays at a readable zoom and
 * starts at the top left of the graph (pan from there). A wide screen, or a graph that fits readably, gets the plain fitted view.
 */
export function fitPhoneView(nodes, size, margin = 60, top = 0, readable = 0.7, narrow = 600, minFit = 0.5) {
  const all = fitView(nodes, size, margin, top);
  const b = bounds(nodes);
  if (!b || size.width >= narrow || all.k >= minFit) return all;   // the whole graph fits at a zoom that can still be read
  return { k: readable, x: 16 - b.x0 * readable, y: top + 16 - b.y0 * readable };
}

/** Initial graph view favors readable labels, even when a large graph needs panning. */
export function fitReadableView(nodes, size, top = 0) {
  const b = bounds(nodes);
  if (!b) return { x: 20, y: 10, k: 0.85 };
  const height = size.height - top;
  const widthFit = (size.width - 100) / (b.x1 - b.x0);
  const heightFit = (height - 100) / (b.y1 - b.y0);
  const fitted = Math.min(1, widthFit, heightFit);
  const k = clamp(Math.max(0.8, fitted), MIN_ZOOM, 1);
  const x = fitted < 0.8 && widthFit < heightFit ? 20 - b.x0 * k : (size.width - (b.x1 - b.x0) * k) / 2 - b.x0 * k;
  return { k, x, y: top + (height - (b.y1 - b.y0) * k) / 2 - b.y0 * k };
}

/** Nodes touched by a rectangle given by two world points. */
export function nodesInRect(nodes, a, b) {
  const x0 = Math.min(a.x, b.x), x1 = Math.max(a.x, b.x), y0 = Math.min(a.y, b.y), y1 = Math.max(a.y, b.y);
  return nodes.filter((n) => !n.proxy && n.x < x1 && n.x + nodeWidth(n) > x0 && n.y < y1 && n.y + nodeHeight(n) > y0);
}

const pos = (n) => ({ x: n.x, y: n.y });

/** Aligns nodes on one axis (to the smallest value). */
export function align(nodes, axis) {
  if (!nodes.length) return new Map();
  const v = Math.min(...nodes.map((n) => n[axis]));
  return new Map(nodes.map((n) => [n.id, { ...pos(n), [axis]: v }]));
}

/** Spreads three or more nodes evenly between the outer ones on one axis. */
export function distribute(nodes, axis) {
  const sorted = [...nodes].sort((a, b) => a[axis] - b[axis]);
  if (sorted.length < 3) return new Map();
  const lo = sorted[0][axis], hi = sorted[sorted.length - 1][axis], step = (hi - lo) / (sorted.length - 1);
  return new Map(sorted.map((n, i) => [n.id, { ...pos(n), [axis]: Math.round(lo + step * i) }]));
}

/**
 * Positions for blocks that have no layout line: columns account for their widest card,
 * with compact spacing so the initial graph remains readable. Deterministic.
 * @param {{name:string, froms:{block:string}[]}[]} blocks
 */
/** The room kept above the cards that a longer line passes over. */
const LANE = 120;

export function autoLayout(blocks, templates) {
  const byName = new Map(blocks.map((b) => [b.name, b]));
  const depth = new Map();
  const walk = (name, trail) => {
    if (depth.has(name)) return depth.get(name);
    if (trail.has(name)) return 0;
    trail.add(name);
    const b = byName.get(name);
    const d = b && b.froms.length ? 1 + Math.max(...b.froms.map((f) => (byName.has(f.block) ? walk(f.block, trail) : -1))) : 0;
    trail.delete(name);
    depth.set(name, Math.max(d, 0));
    return depth.get(name);
  };
  const columns = new Map();
  for (const b of blocks) {
    const d = walk(b.name, new Set());
    const template = templates[b.type];
    const card = { title: b.name, tpl: b.type, ins: template?.inputs || [], outs: template?.outputs || [] };
    const items = columns.get(d) || [];
    items.push({ block: b, width: nodeWidth(card), height: nodeHeight(card) });
    columns.set(d, items);
  }
  // a line that jumps over columns (the dry sound past an effect chain) runs straight through the cards in between: those start lower, so the line has its own lane above them
  const jumped = new Set();
  for (const b of blocks) {
    for (const f of b.froms) {
      if (!byName.has(f.block)) continue;
      for (let c = depth.get(f.block) + 1; c < depth.get(b.name); c++) jumped.add(c);
    }
  }
  const out = new Map();
  let x = 20;
  for (const [column, items] of [...columns].sort(([a], [b]) => a - b)) {
    let y = jumped.has(column) ? 20 + LANE : 20;
    for (const item of items) { out.set(item.block.name, { x, y }); y += item.height + 24; }
    x += Math.max(...items.map((item) => item.width)) + 72;
  }
  return out;
}

/** Controls: a general control fits every control port, a typed one (control<Hz>) only a port of the same unit; the same rule as the language (ControlTypes.Compatible). */
function controlsFit(a, b) {
  if (!String(a).startsWith('control') || !String(b).startsWith('control')) return false;
  return a === 'control' || b === 'control' || a === b;
}
