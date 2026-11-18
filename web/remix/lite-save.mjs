// Remix: saving the text of the editor, and the right to build. Pure (the write and the page's reactions are passed in), so the races are tested in Node.
//
// The saver keeps the NEWEST text of every file of every project that is not in the store yet (a text that was typed, or whose save failed). Saves run one after the
// other, never two at once; flush() answers when what was dirty when it was called, and what came in while it ran, has been tried once. A failed text stays dirty
// (it can be tried again and it is what a build takes); a newer text of the same file replaces it and a late success of an older write never marks a newer text clean.

const keyOf = (projectId, path) => `${projectId}\u0000${path}`;

/**
 * @param {{ write: (projectId: string, path: string, text: string) => Promise<void>, onSaved?: (entry: object) => void, onState?: (state: { failed: object[], dirty: number }) => void }} deps
 */
export function createSaver({ write, onSaved = () => {}, onState = () => {} }) {
  const dirty = new Map();
  let chain = null, seq = 0;

  async function drain() {
    const tried = new Set();
    for (;;) {
      const next = [...dirty].find(([key, entry]) => !tried.has(`${key}#${entry.seq}`));
      if (!next) break;
      const [key, entry] = next;
      tried.add(`${key}#${entry.seq}`);
      try {
        await write(entry.projectId, entry.path, entry.text);
        if (dirty.get(key) === entry) { dirty.delete(key); onSaved(entry); }   // a newer text came in while it was written: that one stays dirty and is saved next
      } catch (error) {
        if (dirty.get(key) === entry) entry.error = error;
      }
    }
    const failed = [...dirty.values()].filter((e) => e.error);
    if (tried.size > 0) onState({ failed, dirty: dirty.size });   // a flush with nothing to save says nothing: the label must not claim "saved" for text that is not queued yet
    return { ok: failed.length === 0, failed };
  }

  return {
    /** A new text of a file: it replaces an older one (also a failed one) of the same file. */
    queue(projectId, path, text) {
      dirty.set(keyOf(projectId, path), { projectId, path, text, seq: ++seq, error: null });
      onState({ failed: [...dirty.values()].filter((e) => e.error), dirty: dirty.size });
    },
    /** Saves what is dirty, after the saves that are on their way; each text is tried once. Never rejects. */
    flush() {
      chain = (chain ?? Promise.resolve()).then(drain);
      return chain;
    },
    /** The newest text of a file that is not in the store yet, or undefined. */
    get: (projectId, path) => dirty.get(keyOf(projectId, path))?.text,
    /** The files of a snapshot of the store with the newest text of the dirty ones: a build takes what is in the editor, saved or not. */
    withDirty(projectId, files) {
      const newest = new Map([...dirty.values()].filter((e) => e.projectId === projectId).map((e) => [e.path, e.text]));
      return newest.size ? files.map((f) => (f.text !== undefined && newest.has(f.path) ? { ...f, text: newest.get(f.path) } : f)) : files;
    },
    /** A file was deleted or renamed: its text is not to be saved any more. */
    drop(projectId, path) { dirty.delete(keyOf(projectId, path)); },
    clear() { dirty.clear(); },
    failed: () => [...dirty.values()].filter((e) => e.error),
    size: () => dirty.size,
  };
}

/**
 * The right to build, one at a time. reserve() is synchronous (call it before the first await), so two quick calls cannot both pass; every build has its own ticket
 * and its own AbortController, and an old ticket can neither free nor abort a newer build.
 */
export function createBuildGate() {
  let current = null, seq = 0;
  return {
    /** A ticket { seq, kind, controller }, or null when a build runs. */
    reserve(kind) {
      if (current) return null;
      current = { seq: ++seq, kind, controller: new AbortController() };
      return current;
    },
    /** Gives the ticket back; true when it was the current one. */
    release(ticket) {
      if (current !== ticket) return false;
      current = null;
      return true;
    },
    busy: () => current !== null,
    kind: () => current?.kind ?? null,
    controller: () => current?.controller ?? null,
    /** Aborts the build that runs; with `onlyKind` only when it is of that kind (Stop of Live Preview never aborts a build of the person). */
    abort(onlyKind) {
      if (current && (!onlyKind || current.kind === onlyKind)) { current.controller.abort(); return true; }
      return false;
    },
  };
}
