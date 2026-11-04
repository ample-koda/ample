// What a draft of Koda changes, read from the unified diff of its task: the blocks it adds (with where their input comes from), so the board can show them dashed before anything is accepted. Pure. @ample:koda

import { HEADER_TAIL } from './block-header.mjs';

const HEADER = new RegExp(String.raw`^([A-Za-z][\w-]*)\s+([A-Za-z][\w-]*)` + HEADER_TAIL);
const FROM = /^\s*from\s+([A-Za-z][\w-]*)\.([\w-]+)\s*->\s*([\w-]+)/;

/** The added blocks of a unified diff: [{ type, name, from: [{ block, output, input }] }]. A block counts when its header line and its closing brace were both added. */
export function addedBlocks(diff) {
  const blocks = [];
  let open = null;
  for (const raw of String(diff ?? '').split('\n')) {
    if (raw.startsWith('+++') || raw.startsWith('---')) continue;
    if (!raw.startsWith('+')) { open = null; continue; }
    const line = raw.slice(1);
    const header = HEADER.exec(line.trim());
    if (header && !line.startsWith(' ') && !line.startsWith('\t')) { open = { type: header[1], name: header[2], from: [] }; blocks.push(open); continue; }
    const from = open && FROM.exec(line);
    if (from) open.from.push({ block: from[1], output: from[2], input: from[3] });
  }
  return blocks;
}

/** Where each ghost goes: to the right of the block it reads from (below the ghosts already there), else in a free corner. nodes: [{ id, x, y, w, h }]. */
export function ghostSpots(ghosts, nodes, gap = 60, rowHeight = 110) {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  const used = new Map();
  const spots = [];
  let free = 0;
  for (const g of ghosts) {
    const anchor = g.from.map((f) => byId.get(f.block)).find(Boolean);
    if (anchor) {
      const k = used.get(anchor.id) ?? 0;
      used.set(anchor.id, k + 1);
      spots.push({ ...g, x: anchor.x + anchor.w + gap, y: anchor.y + k * rowHeight, anchor: anchor.id });
    } else {
      spots.push({ ...g, x: 40 + free * 40, y: 40 + free * rowHeight, anchor: null });
      free++;
    }
  }
  return spots;
}
