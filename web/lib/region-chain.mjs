/**
 * The effects a region has passed through: from the block that gives the region (a loop) down the chain of blocks that take it as region_in.
 * blocks: the blocks of the song ({ name, type, froms: [{ block, output, input, disabled }] }); name: the block the port sits on;
 * includeSelf: true for an output of an effect (its own effect counts), false for an input or an output of the source (only what comes before it).
 * Returns { source, effects: [names, first to last] }; source is the block that gives the region ('' when it cannot be found).
 */
export function regionChain(blocks, name, includeSelf) {
  const byName = new Map((blocks || []).map((b) => [b.name, b]));
  const linkOf = (b) => (b?.froms || []).find((f) => !f.disabled && f.input === 'region_in');
  const effects = [];
  const seen = new Set();
  let cur = byName.get(name);
  if (!cur) return { source: '', effects };
  let link = linkOf(cur);
  if (!link) return { source: cur.name, effects };   // the block gives the region itself
  if (includeSelf) effects.push(cur.name);
  while (link && !seen.has(cur.name)) {
    seen.add(cur.name);
    const up = byName.get(link.block);
    if (!up) return { source: '', effects: effects.reverse() };
    const next = linkOf(up);
    if (!next) return { source: up.name, effects: effects.reverse() };
    effects.push(up.name);
    cur = up;
    link = next;
  }
  return { source: '', effects: effects.reverse() };
}

/** The line that tells which effects a region has passed, for a tooltip ("2 effects: reverb, filter"); empty when none. */
export const chainText = (chain) => (chain.effects.length ? `${chain.effects.length} effect${chain.effects.length === 1 ? '' : 's'}: ${chain.effects.join(', ')}` : '');
