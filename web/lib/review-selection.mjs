// Which checked files of an Assistant review still stand: a check belongs to the diff that was on the screen when it was made. When a follow-up changes
// the diff of a file, the check is dropped, so it cannot approve text the person has not read. Pure, so node --test checks it.

/**
 * @param {Iterable<string>} selected the paths that are checked
 * @param {Map<string, string>} reviewed path -> the diff that was shown when it was checked
 * @param {Array<{path: string, diff?: string}>} changes the changes of the proposal now
 * @returns {string[]} the checked paths whose diff is not the one that was reviewed (or that are gone)
 */
export function staleSelections(selected, reviewed, changes) {
  const now = new Map((changes ?? []).map((c) => [c.path, c.diff ?? '']));
  return [...selected].filter((path) => !now.has(path) || reviewed.get(path) !== now.get(path));
}
