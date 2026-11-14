// Which of the files of a Koda proposal are other files to look at, and which are deletions. A deletion is never shown as a file that is empty.
// The server says a deleted file with status 'deleted' (and no text after); an older shape had a `deleted` flag. Pure, so node --test checks it.

export const isDeleted = (change) => change?.status === 'deleted' || change?.deleted === true;

/**
 * @param {Array<{path: string, status?: string, deleted?: boolean}>} changes the changes of the proposal
 * @param {string} activePath the song that is open
 * @returns {{ others: object[], deleted: object[] }} the other files that are made or changed, and the files that are removed
 */
export function splitChanges(changes, activePath) {
  const list = (changes ?? []).filter((c) => c?.path);
  return {
    others: list.filter((c) => c.path !== activePath && !isDeleted(c)),
    deleted: list.filter((c) => isDeleted(c)),
  };
}

/** Whether the files can come into the editor as unsaved text: not when something is deleted, and not when another song is among them. */
export const canKeepAsUnsaved = ({ others, deleted }) => deleted.length === 0 && others.length > 0 && !others.some((c) => /\.amplet$/i.test(c.path));
