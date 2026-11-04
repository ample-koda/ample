// Variants the model offered with the MCP tool ample_offer_variants: all of them are in the one proposal, each names the blocks that belong to it. Here that one proposal is cut into a draft per variant:
// the changes of its own blocks, plus the changes that belong to no variant (shared by all). A change in a block outside the variants (the mixer that takes the new blocks in) is cut per line:
// a line that names a block of another variant stays out. @ample:koda
import { describeChanges, structuredHunks } from './koda-diff.mjs';
import { applyHunks } from './koda-lines.mjs';

const escape = (name) => name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** variants: [{ name, summary, blocks: [name], listen }] -> [{ name, note, summary: [change], text, listen }] */
export function variantDrafts(base, proposed, variants) {
  const items = describeChanges(base, proposed);
  const hunks = structuredHunks(base, proposed);
  const claimed = new Set(variants.flatMap((v) => v.blocks));
  return variants.map((v) => {
    const own = new Set(v.blocks);
    const others = [...claimed].filter((n) => !own.has(n));
    const names = others.map((n) => new RegExp(`(?<![\\w-])${escape(n)}(?![\\w-])`));
    const mentions = (line) => names.some((re) => re.test(String(line)));
    const kept = [];
    const dropped = new Set();
    hunks.forEach((h, i) => {
      if (h.block && claimed.has(h.block) && !own.has(h.block)) { dropped.add(i); return; }   // a block of another variant
      const added = h.added.filter((line) => !mentions(line));   // a link to a block of another variant
      if (added.length < h.added.length && h.removed === 0 && added.length === 0) { dropped.add(i); return; }
      kept.push({ ...h, added });
    });
    return {
      name: v.name, note: v.summary || '', listen: v.listen || v.blocks[v.blocks.length - 1],
      summary: items.filter((it) => !dropped.has(it.hunk) && !mentions(it.text)),
      text: applyHunks(base, kept),
    };
  });
}
