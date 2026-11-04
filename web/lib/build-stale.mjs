// Which built blocks a change to the song makes out of date: the blocks that changed and the blocks that read them (their takes are the inputs of the consumers), nothing else.
// The engine already does this with its plan ids; this is the same decision for the built state the editor shows. @ample:build-stale

const part = (b) => (b.parts.length ? Math.min(...b.parts.map((p) => p.at)) : 1);

/** Everything that changes every block at once: the project header (without the master seed), the song settings and the module uses. */
export const globalSignature = (doc) => JSON.stringify([{ ...doc.project, masterSeed: undefined }, doc.settings, doc.uses]);

/** What decides how one block sounds: its own text, with its clip positions relative to the first clip (moving a clip as a whole does not change the block). */
export const blockSignature = (b) => JSON.stringify([
  b.type, b.name, b.class, b.version, b.flags, b.freeze,
  b.parts.map((p) => [p.at - part(b) + 1, p.length, p.name]),
  b.parameters.filter((p) => p.name !== 'tint').map((p) => [p.name, p.value]),   // the tint only colours the card
  b.froms.map((f) => [f.block, f.output, f.input, f.disabled]),
]);

export const signatureOf = (doc) => ({ global: globalSignature(doc), master: doc.project?.masterSeed, blocks: Object.fromEntries(doc.blocks.map((b) => [b.name, blockSignature(b)])) });

const partsOf = (b) => JSON.stringify(b.parts);

/**
 * The names of the blocks whose build is out of date after `next` replaces what `built` was built from, or 'all' when nothing can be kept.
 * `built` is signatureOf() of the built song (or null when nothing was built), `previous` and `next` are the documents; the arranged mix (mixer, master) and
 * what reads it also follow when a clip moves, and `followsMaster` (names, from GET /api/doc/inputs) limits a master seed change to the blocks that derive their seed from it, because the positions decide where the clips land in the mix.
 */
export function staleBlocks(built, previous, next, followsMaster = null) {
  if (!built || built.global !== globalSignature(next)) return 'all';
  const stale = new Set();
  if (built.master !== next.project?.masterSeed) {
    // only the blocks whose seed follows the master seed change with it; without that knowledge every block does
    if (!followsMaster) return 'all';
    for (const name of followsMaster) stale.add(name);
  }
  for (const b of next.blocks) if (built.blocks[b.name] !== blockSignature(b)) stale.add(b.name);
  if (previous) {
    const before = new Map(previous.blocks.map((b) => [b.name, partsOf(b)]));
    if (next.blocks.some((b) => before.get(b.name) !== undefined && before.get(b.name) !== partsOf(b))) {
      for (const b of next.blocks) if (b.type === 'mixer' || b.type === 'bus' || b.type === 'master') stale.add(b.name);
    }
  }
  return withReaders(next, stale);
}

/** The blocks that read a changed block (through their from lines), and the ones that read those: the changed set grown to everything downstream of it. */
export function withReaders(doc, changed) {
  const stale = new Set(changed);
  for (let grew = true; grew;) {
    grew = false;
    for (const b of doc.blocks) if (!stale.has(b.name) && b.froms.some((f) => stale.has(f.block))) { stale.add(b.name); grew = true; }
  }
  return stale;
}

/**
 * The blocks that are out of date because a file they read changed (a sample, a lyrics or MIDI file, a SoundFont): before and after map a block name to the stamp
 * of its files (GET /api/doc/inputs, field blocks), a block without files has no entry. Null before means nothing is known yet, so nothing is stale.
 */
export function staleByFiles(before, after, doc) {
  if (!before) return new Set();
  const changed = new Set();
  for (const name of new Set([...Object.keys(before), ...Object.keys(after)])) if (before[name] !== after[name]) changed.add(name);
  return withReaders(doc, changed);
}
