// How the undo list looks after Koda has worked in the live view (bare metal). Pure (no DOM, no store), so it is tested in node.
// The assistant's edits and the person's own edits go into the same undo list. When Koda is done its steps are folded into one,
// but that used to cut every entry after the first one, so the person's own edits made meanwhile were cut too.

/**
 * The undo list with the steps of the assistant folded into one.
 * @param {string[]} undo the undo list now (each entry is the text from before an edit)
 * @param {number | null} start the length of the list when the assistant made its first edit (null: it made none)
 * @param {number | null} firstUser the index of the first entry that the person made meanwhile (null: the person made none)
 * @returns {string[]} a new list: up to and including the first step of the assistant, then, when the person edited meanwhile, every entry
 *   from the first manual edit on (the steps of the assistant in between are folded away, the person's edits stay separately undoable)
 */
export function foldAssistantSteps(undo, start, firstUser = null) {
  const list = Array.isArray(undo) ? undo.slice() : [];
  if (start === null || start === undefined || start < 0) return list;
  const keepFrom = firstUser === null || firstUser === undefined ? list.length : Math.min(Math.max(firstUser, start + 1), list.length);
  if (keepFrom <= start + 1) return list;
  return [...list.slice(0, start + 1), ...list.slice(keepFrom)];
}
