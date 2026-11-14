// The order in which a selection of blocks is removed. The server refuses to remove a block that another block still reads from, so a block goes
// only after the blocks that read it (those among the selection). Blocks that read nothing of the selection can go first, in the order they were
// selected. Pure, so node --test checks it.

/**
 * @param {string[]} ids the selected blocks, in the order they were selected
 * @param {Array<{from: string, to: string}>} links the cables of the song
 * @returns {string[]} the same blocks, readers before the blocks they read; blocks that read each other (which a valid song never has) keep their order at the end
 */
export function removalOrder(ids, links) {
  const remaining = [...new Set(ids)];
  const out = [];
  while (remaining.length) {
    const free = remaining.find((id) => !links.some((l) => l.from === id && l.to !== id && remaining.includes(l.to)));
    if (free === undefined) { out.push(...remaining); break; }   // a cycle: the server will say so
    out.push(free);
    remaining.splice(remaining.indexOf(free), 1);
  }
  return out;
}
