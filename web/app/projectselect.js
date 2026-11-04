// Selecting several items of the project panel with a rectangle, and moving a selection into a folder with a confirmation that says what will happen first.
import { api } from './api.js';
import { ask, el, toast } from './ui.js';
import { marqueeHits, planMoves } from '../lib/project-selection-model.mjs';

const MOVED = 4;   // pixels the pointer must travel before it is a rectangle and not a click

/**
 * Lets the pointer draw a rectangle on empty space of `scroller` (the element that scrolls); the items it touches become the selection, like in a file manager.
 * Near the top or bottom edge the list scrolls on by itself while the button stays down, and the rectangle keeps growing.
 * hooks.targets() gives the elements of the items (they carry data-path), hooks.current() the selection now, hooks.set(paths, last) takes the new one.
 * hooks.rows() may give { paths, rowHeight, top } for a plain list whose rows are not all on the page (a long list that is drawn in a window): the rows
 * the rectangle crosses are then worked out from their place and not from the elements.
 */
export function attachMarquee(scroller, hooks) {
  scroller.addEventListener('mousedown', (event) => {
    if (event.button !== 0 || event.target.closest('.project-tile, tr[data-path], thead, button, input, a, select')) return;
    const first = scroller.getBoundingClientRect();
    if (event.clientX - first.left > scroller.clientWidth || event.clientY - first.top > scroller.clientHeight) return;   // a scrollbar
    const additive = event.ctrlKey || event.metaKey || event.shiftKey;
    const base = additive ? new Set(hooks.current()) : new Set();
    const toContent = (clientX, clientY) => {
      const area = scroller.getBoundingClientRect();
      return { x: clientX - area.left + scroller.scrollLeft, y: clientY - area.top + scroller.scrollTop };
    };
    const start = toContent(event.clientX, event.clientY);
    let pointer = { x: event.clientX, y: event.clientY };
    let box = null, moved = false, frame = 0;
    if (getComputedStyle(scroller).position === 'static') scroller.style.position = 'relative';
    event.preventDefault();   // no text selection while the rectangle is drawn

    const update = () => {
      const here = toContent(pointer.x, pointer.y);
      if (!moved && Math.hypot(here.x - start.x, here.y - start.y) < MOVED) return false;
      moved = true;
      if (!box) { box = el('div', { className: 'project-marquee', 'aria-hidden': 'true' }); scroller.append(box); }
      const rect = { left: Math.min(start.x, here.x), top: Math.min(start.y, here.y), right: Math.max(start.x, here.x), bottom: Math.max(start.y, here.y) };
      Object.assign(box.style, { left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.right - rect.left}px`, height: `${rect.bottom - rect.top}px` });
      const rows = hooks.rows?.();
      let hits;
      if (rows && rows.paths.length) {
        const from = Math.max(0, Math.floor((rect.top - rows.top) / rows.rowHeight));
        const to = Math.min(rows.paths.length - 1, Math.floor((rect.bottom - rows.top) / rows.rowHeight));
        hits = from <= to ? rows.paths.slice(from, to + 1) : [];
      } else {
        const area = scroller.getBoundingClientRect();
        const boxes = hooks.targets().map((node) => {
          const r = node.getBoundingClientRect();
          return { path: node.dataset.path, left: r.left - area.left + scroller.scrollLeft, right: r.right - area.left + scroller.scrollLeft, top: r.top - area.top + scroller.scrollTop, bottom: r.bottom - area.top + scroller.scrollTop };
        });
        hits = marqueeHits(rect, boxes);
      }
      hooks.set(new Set([...base, ...hits]), hits[hits.length - 1] || '');
      return true;
    };

    // while the button is down and the pointer is near an edge the list scrolls, faster the closer to (or beyond) the edge
    const EDGE = 32;
    const scrollOn = () => {
      if (moved) {
        const area = scroller.getBoundingClientRect();
        let step = 0;
        if (pointer.y < area.top + EDGE) step = -(area.top + EDGE - pointer.y);
        else if (pointer.y > area.bottom - EDGE) step = pointer.y - (area.bottom - EDGE);
        step = Math.max(-48, Math.min(48, Math.round(step / 2)));
        if (step) { scroller.scrollTop += step; update(); }
      }
      frame = requestAnimationFrame(scrollOn);
    };
    const move = (e) => { pointer = { x: e.clientX, y: e.clientY }; update(); };
    const end = () => {
      document.removeEventListener('mousemove', move);
      document.removeEventListener('mouseup', end);
      cancelAnimationFrame(frame);
      box?.remove();
      if (!moved && !additive) hooks.set(new Set(), '');   // a click on empty space clears the selection
    };
    document.addEventListener('mousemove', move);
    document.addEventListener('mouseup', end);
    frame = requestAnimationFrame(scrollOn);
  });
}

const counted = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;

/**
 * Moves the items into a folder, the way a move with refactor does for one item: the songs and modules that point at them follow.
 * Before anything happens a dialog lists every move, the references that will be updated, and what is left alone and why.
 * Returns true when something was moved.
 */
export async function moveSelection(sources, destination, { refresh, reloadDoc }) {
  let existing = new Set();
  try { existing = new Set((await api.get(`/api/project/tree?path=${encodeURIComponent(destination)}&depth=1`)).children.map((child) => child.name)); } catch { /* an unreadable folder is reported by the move itself */ }
  const { moves, skipped } = planMoves(sources, destination, existing);
  const where = destination || 'the project root';
  if (!moves.length) {
    toast(skipped.length ? `Nothing to move into ${where}: ${skipped[0].path} ${skipped[0].reason}.` : 'Nothing to move.');
    return false;
  }
  const plan = [];
  let references = 0;
  for (const move of moves) {
    let edits = [];
    try { edits = (await api.post('/api/project/rename', { source: move.from, destination: move.to, dryRun: true })).edits || []; } catch (error) { skipped.push({ path: move.from, reason: error.message || 'cannot be moved' }); continue; }
    references += edits.length;
    plan.push({ ...move, edits: edits.length });
  }
  if (!plan.length) { toast(`Nothing could be moved into ${where}.`); return false; }

  const lines = [
    ...plan.map((move) => `${move.from}  →  ${move.to}${move.edits ? `   (${counted(move.edits, 'reference')} updated)` : ''}`),
    ...(skipped.length ? ['', 'Left alone:', ...skipped.map((item) => `${item.path}: ${item.reason}`)] : []),
  ];
  const summary = `${counted(plan.length, 'item')} will move into ${where}. ${references ? `${counted(references, 'line')} in your songs and modules that point at them will be updated.` : 'Nothing in the songs or modules points at them.'} You can undo it from the history.`;
  const list = el('pre', { className: 'refactor-list', id: 'move-plan', textContent: lines.join('\n') });
  if (!(await ask(`Move ${counted(plan.length, 'item')}`, summary, [{ label: 'Move', value: true, primary: true }, { label: 'Cancel', value: false }], [list]))) return false;

  let done = 0, updated = 0;
  const failed = [];
  for (const move of plan) {
    try {
      const result = await api.post('/api/project/rename', { source: move.from, destination: move.to, refactor: true });
      done += 1;
      updated += (result.edits || []).length;
    } catch (error) { failed.push(`${move.from}: ${error.message || error}`); }
  }
  await refresh();
  if (updated) await reloadDoc().catch(() => {});   // the open song may have changed
  toast(failed.length ? `Moved ${done} of ${plan.length}. ${failed[0]}` : `Moved ${counted(done, 'item')} into ${where}; ${counted(updated, 'reference')} updated.`);
  return done > 0;
}
