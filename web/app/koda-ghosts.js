// The draft of Koda on the block board: the blocks it would add, drawn dashed next to the blocks they read from, with a dashed line. Nothing is in the song yet; they go when the draft is decided. @ample:koda
import { flat } from './store.js';
import { nodeHeight, nodeWidth } from './geometry.js';
import { el } from './ui.js';
import { ghostSpots } from '../lib/koda-draft.mjs';

let layer = null;

export function clearGhosts() { layer?.remove(); layer = null; }

/** ghosts: [{ type, name, from: [...] }] from addedBlocks(). */
export function showGhosts(ghosts) {
  clearGhosts();
  const world = document.querySelector('#world');
  if (!world || !ghosts?.length) return;
  const nodes = flat().nodes.filter((n) => !n.proxy).map((n) => ({ id: n.id, x: n.x, y: n.y, w: nodeWidth(n), h: nodeHeight(n) }));
  const spots = ghostSpots(ghosts, nodes);
  layer = el('div', { className: 'koda-ghosts', 'aria-hidden': 'true' });
  const ns = 'http://www.w3.org/2000/svg';
  const svg = document.createElementNS(ns, 'svg');
  svg.setAttribute('class', 'koda-ghost-lines');
  for (const s of spots) {
    const card = el('div', { className: 'koda-ghost', title: `Koda's draft: a new ${s.type} named ${s.name}. Nothing is changed until you keep it.`, style: `left:${s.x}px;top:${s.y}px` }, [el('i', { textContent: 'draft' }), el('b', { textContent: s.name }), el('span', { textContent: s.type })]);
    layer.append(card);
    const anchor = s.anchor ? nodes.find((n) => n.id === s.anchor) : null;
    if (anchor) {
      const path = document.createElementNS(ns, 'path');
      const x1 = anchor.x + anchor.w, y1 = anchor.y + 28, x2 = s.x, y2 = s.y + 28;
      path.setAttribute('d', `M${x1} ${y1}C${x1 + 30} ${y1} ${x2 - 30} ${y2} ${x2} ${y2}`);
      svg.append(path);
    }
  }
  layer.prepend(svg);
  world.append(layer);
}
