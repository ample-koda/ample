// Which work is not saved yet, over every place that can hold it. Pure (no DOM, no storage, no network), so it is tested in node.
// The branch switch used to look at the song that has focus only, so a changed song in another tab, a changed module or text file, or a
// recovery buffer of this browser went unnoticed and was left behind.

const byPath = (a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0);

/**
 * The paths with unsaved work and why each one counts.
 * - dirtyTabs: paths of the open tabs whose working copy differs from its file (the song that is open included)
 * - drafts: recovery buffers of text editors ({ path, text, baseText }); only one that differs from its base text counts
 * @returns {{ path: string, reasons: string[] }[]} sorted by path, every path once
 */
export function unsavedWork({ dirtyTabs = [], drafts = [] } = {}) {
  const found = new Map();
  const add = (path, reason) => {
    if (typeof path !== 'string' || !path) return;
    if (!found.has(path)) found.set(path, new Set());
    found.get(path).add(reason);
  };
  for (const path of dirtyTabs) add(path, 'changed in an open tab');
  for (const draft of drafts) {
    if (draft && typeof draft.text === 'string' && draft.text !== draft.baseText) add(draft.path, 'unsaved text kept in this browser');
  }
  return [...found].map(([path, reasons]) => ({ path, reasons: [...reasons] })).sort(byPath);
}

const baseName = (path) => path.split('/').pop();

/** The question before the work is left behind: how many files, and which. */
export function unsavedMessage(items, action = 'Going to another branch leaves them behind.') {
  const names = items.map((item) => baseName(item.path));
  const shown = names.length > 6 ? `${names.slice(0, 6).join(', ')} and ${names.length - 6} more` : names.join(', ');
  return `${items.length === 1 ? '1 file has' : `${items.length} files have`} changes that are not saved: ${shown}. ${action}`;
}

/**
 * What is left after a save: the items that are still unsaved, for the decision "may the switch go on". An empty list means it may.
 * `failed` are the paths whose save threw; they stay in the list even if the state does not show them as changed any more.
 */
export function stillUnsaved(items, failed = []) {
  const failedSet = new Set(failed);
  const found = new Map(items.map((item) => [item.path, item]));
  for (const path of failedSet) if (!found.has(path)) found.set(path, { path, reasons: ['the save failed'] });
  return [...found.values()].sort(byPath);
}
