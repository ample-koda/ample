// When the Sequencer may write the sequence to disk (or into the song), and what Undo / Revert must put back.
// The state is the Sequencer's own: { snapshot, original, wroteLive, embedded }.

/** A locked block (or a song that is read-only) is never written: not by Play, Live, Apply, Save or lane routing. */
export const mayWrite = ({ readOnly = false, locked = false } = {}) => !readOnly && !locked;

/**
 * What Play does to get a take.
 * - reuse: the take that is there is current, or the block is locked and this is the audio there is
 * - write-and-build: write the staged text, then build (the write is marked, so Undo can put the file back)
 * - build-only: locked, no take yet: build what is already on disk, write nothing
 */
export function playPlan({ locked, hasTake, stale }) {
  if (hasTake && (!stale || locked)) return 'reuse';
  return locked ? 'build-only' : 'write-and-build';
}

/** The state after the staged text was written out for Play, Build or Live (not for a Save or Apply): it has to be undone later. */
export const afterStagedWrite = (state, text) => ({ ...state, snapshot: text, wroteLive: true });

/** The state after Save or Apply wrote the text: that text is now the version Undo and Revert go back to. */
export const afterSavedWrite = (state, text) => ({ ...state, snapshot: text, original: text, wroteLive: false });

/** Whether something other than the last saved text is on disk (or in the song), so leaving with Undo must put the saved text back. */
export const needsRestore = ({ snapshot, original, wroteLive }) => snapshot !== original || !!wroteLive;

/** The state after the saved text was put back. */
export const afterRestore = (state) => ({ ...state, snapshot: state.original, wroteLive: false });
