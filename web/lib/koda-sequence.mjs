// What Koda does when the sequencer editor is open and the proposal changes the sequence that is kept in the song: only that sequence changed (it is put in the editor, the person saves it),
// or more changed (the editor has to be closed first). Pure: no DOM. @ample:koda
import { blockRanges } from './template-model.mjs';
import { readEmbedded, withEmbedded } from './embedded-seq.mjs';

const same = (a, b) => String(a).replace(/\s+$/, '') === String(b).replace(/\s+$/, '');

/**
 * The new text of the sequence kept in a block when the proposal changes nothing else: { text }, or null (the proposal changes more than that sequence, or the block has no sequence in the song).
 * base: the song text now; proposed: the song text of the proposal.
 */
export function stagedSequence(base, proposed, block) {
  const before = blockRanges(base).find((r) => r.name === block);
  const after = blockRanges(proposed).find((r) => r.name === block);
  if (!before || !after) return null;
  const text = readEmbedded(proposed, after.start);
  if (text === null) return null;
  return same(withEmbedded(base, before.start, text), proposed) ? { text } : null;
}
