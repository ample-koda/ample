// The small graph of one line for the dock of the Playlist: the notes, the sources, the effects, the bus and the key source of a line as nodes from left to right with
// edges between them. A pure layout (no DOM); the page draws it. It reads the model of playlist-model.mjs. @ample:playlist-graph

const W = 92, H = 26, GX = 22, GY = 10;

/** { nodes: [{ id, label, kind, x, y, w, h }], edges: [{ from, to, dashed }], width, height } for a line of the model; \`keys\` are the key links of the model. */
export function lineGraph(line, keys = []) {
  const columns = [];
  const noteSteps = [...new Set(line.noteSides.flatMap((n) => n.steps))];
  if (noteSteps.length) columns.push(noteSteps.map((id) => ({ id, kind: 'note' })));
  columns.push(line.sources.filter((s) => s.block).map((s) => ({ id: s.block, kind: 'source' })));
  const pre = [...new Set(line.sources.flatMap((s) => s.pre))];
  for (const id of pre) columns.push([{ id, kind: 'chain' }]);
  for (const id of line.chain) if (!pre.includes(id)) columns.push([{ id, kind: 'chain' }]);
  const nodes = [], edges = [];
  const byId = new Map();
  columns.forEach((col, ci) => col.forEach((n, ri) => {
    const node = { id: n.id, label: n.id, kind: n.kind, x: ci * (W + GX), y: ri * (H + GY) + (keys.length ? H + GY : 0), w: W, h: H };
    nodes.push(node); byId.set(n.id, node);
  }));
  // the signal path: notes into their instrument, sources into the chain, one chain step into the next
  for (const side of line.noteSides) {
    const steps = side.steps;
    for (let i = 1; i < steps.length; i++) edges.push({ from: steps[i - 1], to: steps[i], dashed: false });
    if (steps.length && byId.has(side.instrument)) edges.push({ from: steps.at(-1), to: side.instrument, dashed: false });
  }
  const first = columns[(noteSteps.length ? 1 : 0) + 1]?.[0]?.id ?? null;   // the first step after the sources
  for (const s of line.sources) if (s.block && first && s.block !== first) edges.push({ from: s.block, to: first, dashed: false });
  const order = [...pre, ...line.chain.filter((c) => !pre.includes(c))];
  for (let i = 1; i < order.length; i++) edges.push({ from: order[i - 1], to: order[i], dashed: false });
  // a sidechain key into one of the blocks: a node above, with a dashed edge
  keys.filter((k) => byId.has(k.toBlock)).forEach((k, i) => {
    const node = { id: `key:${k.fromBlock}`, label: `key ${k.fromBlock}`, kind: 'key', x: byId.get(k.toBlock).x, y: i * (H + GY), w: W, h: H, block: k.fromBlock };
    nodes.push(node);
    edges.push({ from: node.id, to: k.toBlock, dashed: true });
  });
  const width = Math.max(0, ...nodes.map((n) => n.x + n.w)), height = Math.max(0, ...nodes.map((n) => n.y + n.h));
  return { nodes, edges: edges.filter((e) => nodes.some((n) => n.id === e.from) && nodes.some((n) => n.id === e.to)), width, height };
}
