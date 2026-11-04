// Selection and move rules of the project panel (no DOM): what a click, Ctrl+click, Shift+click and a rectangle select, and what moving a selection into a folder will do.

/** The new selection after a click on `path`. `order` is the visible items in screen order, `anchor` the item the last plain or Ctrl click was on. */
export function clickSelection(order, current, anchor, path, event = {}) {
  const additive = !!(event.ctrlKey || event.metaKey);
  if (event.shiftKey && anchor && order.includes(anchor) && order.includes(path)) {
    const from = order.indexOf(anchor), to = order.indexOf(path);
    const range = order.slice(Math.min(from, to), Math.max(from, to) + 1);
    return { selected: new Set(additive ? [...current, ...range] : range), anchor };
  }
  if (additive) {
    const next = new Set(current);
    if (next.has(path)) next.delete(path); else next.add(path);
    return { selected: next, anchor: path };
  }
  return { selected: new Set([path]), anchor: path };
}

/** The paths whose boxes touch the rectangle. Boxes and rectangle are {left, top, right, bottom}. */
export function marqueeHits(rect, boxes) {
  const left = Math.min(rect.left, rect.right), right = Math.max(rect.left, rect.right);
  const top = Math.min(rect.top, rect.bottom), bottom = Math.max(rect.top, rect.bottom);
  return boxes.filter((box) => box.right >= left && box.left <= right && box.bottom >= top && box.top <= bottom).map((box) => box.path);
}

const parentOf = (path) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '');
const nameOf = (path) => path.slice(path.lastIndexOf('/') + 1);
const join = (folder, name) => (folder ? `${folder}/${name}` : name);

/**
 * What moving the selection into a folder would do: the moves that can happen and the items that are left alone, each with the reason.
 * `existing` is the set of names already in the destination folder. An item inside another selected folder moves along with it.
 */
export function planMoves(sources, destination, existing = new Set()) {
  const paths = sources.map((source) => source.path);
  const moves = [], skipped = [];
  const taken = new Set(existing);
  for (const source of sources) {
    const inside = paths.find((other) => other !== source.path && source.path.startsWith(`${other}/`));
    if (inside) { skipped.push({ path: source.path, reason: `moves along with ${inside}` }); continue; }
    if (parentOf(source.path) === destination) { skipped.push({ path: source.path, reason: 'is already in that folder' }); continue; }
    if (source.isDir && (destination === source.path || destination.startsWith(`${source.path}/`))) { skipped.push({ path: source.path, reason: 'cannot move into itself' }); continue; }
    const name = nameOf(source.path);
    if (taken.has(name)) { skipped.push({ path: source.path, reason: `${name} already exists in the destination` }); continue; }
    taken.add(name);
    moves.push({ from: source.path, to: join(destination, name), name, isDir: !!source.isDir });
  }
  return { moves, skipped };
}
