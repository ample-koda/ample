// What the browser keeps of the work in progress, so that a refresh or a restart of the server does not lose what was not saved: the text as it is now with the undo and redo
// history. These are the pure parts (what to keep, how much, what changed); the storage and the question are in store.js. @ample:work-state

const MAX_ENTRIES = 100, MAX_CHARS = 1_500_000;

/** The newest entries of a history that fit in the limits (a text of the song is kept whole, so the number and the total size are limited). */
export function trimHistory(entries, maxEntries = MAX_ENTRIES, maxChars = MAX_CHARS) {
  const kept = [];
  let chars = 0;
  for (let i = entries.length - 1; i >= 0 && kept.length < maxEntries; i--) {
    const entry = entries[i];
    if (typeof entry !== 'string') continue;
    if (chars + entry.length > maxChars) break;
    chars += entry.length;
    kept.unshift(entry);
  }
  return kept;
}

/** The record to keep: the text now with the history, or null when there is nothing worth keeping. */
export function workRecord({ project, path, text, undo, redo, now = Date.now() }) {
  if (typeof text !== 'string') return null;
  return { v: 1, project: project ?? '', path, text, undo: trimHistory(undo ?? []), redo: trimHistory(redo ?? []), updated: now };
}

/** Reads a stored record; null when it is not a record of this kind or of this path. */
export function parseRecord(json, path) {
  try {
    const record = JSON.parse(json);
    if (!record || record.v !== 1 || record.path !== path || typeof record.text !== 'string') return null;
    return { ...record, undo: Array.isArray(record.undo) ? record.undo.filter((e) => typeof e === 'string') : [], redo: Array.isArray(record.redo) ? record.redo.filter((e) => typeof e === 'string') : [] };
  } catch { return null; }
}

/** Lists valid recovery buffers for one project without exposing other projects' browser state. */
export function listWorkRecords(storage, project) {
  const prefix = `ample.work:${project}:`, records = [];
  try {
    for (let index = 0; index < storage.length; index++) {
      const key = storage.key(index);
      if (!key?.startsWith(prefix)) continue;
      try {
        const record = JSON.parse(storage.getItem(key));
        if (record?.project === project && typeof record.path === 'string' && typeof record.text === 'string' && Number.isFinite(record.updated)) records.push(record);
      } catch { /* one damaged browser entry does not hide the others */ }
    }
  } catch { return []; }
  return records.sort((a, b) => b.updated - a.updated);
}

/** Deletes one browser undo buffer after the user chooses the saved file. */
export function removeWorkRecord(storage, project, path) {
  storage.removeItem(`ample.work:${project}:${path}`);
}

/** Remembers that the user explicitly chose to restore a buffer the next time its file opens. */
export function acceptRecoveryOnOpen(storage, project, path, kind, updated, savedText, now = Date.now()) {
  const base = savedText === undefined ? '' : `:${textFingerprint(savedText)}`;
  storage.setItem(`ample.recovery.accept:${project}:${kind}:${path}`, updated === undefined ? '1' : `u${updated}${base}@${now}`);
}

/** A short fingerprint of a text (length + FNV-1a hash), used to bind an acceptance to the saved text it was made against. */
export function textFingerprint(text) {
  const s = String(text);
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
  return `${s.length.toString(36)}-${h.toString(36)}`;
}

/** Removes acceptances older than maxAgeMs (default 30 days) and, when livePaths (a Set of `kind:path`) is given, those whose draft is gone. Returns how many were removed. Stored form: `u<updated>:<fingerprint of saved text>@<time ticked>`; older forms (`1`, `u<updated>`) are still read. */
export function pruneRecoveryAcceptances(storage, project, livePaths, now = Date.now(), maxAgeMs = 30 * 86400000) {
  const prefix = `ample.recovery.accept:${project}:`, doomed = [];
  try {
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (!key?.startsWith(prefix)) continue;
      const at = /@(\d+)$/.exec(storage.getItem(key) || '');
      if ((at && now - Number(at[1]) > maxAgeMs) || (livePaths && !livePaths.has(key.slice(prefix.length)))) doomed.push(key);
    }
    for (const key of doomed) storage.removeItem(key);
  } catch { /* storage unavailable */ }
  return doomed.length;
}

/** Consumes a previous explicit recovery choice for one file and buffer kind. A choice made ahead of time names the buffer it was made for (its update time): a buffer that changed since is asked about again. */
export function consumeRecoveryAcceptance(storage, project, path, kind, updated, savedText) {
  const key = `ample.recovery.accept:${project}:${kind}:${path}`;
  try {
    const value = storage.getItem(key);
    if (value === null) return false;
    storage.removeItem(key);
    if (value === '1') return true;
    const m = /^u(\d+)(?::([^@]+))?(?:@\d+)?$/.exec(value);
    if (!m || updated === undefined || m[1] !== String(updated)) return false;
    // bound to the saved text it was made against: the file changed on disk since, so ask again
    if (m[2] && (savedText === undefined || m[2] !== textFingerprint(savedText))) return false;
    return true;
  } catch { return false; }
}

/** How many lines differ between two texts (lines that were added, removed or changed, counted by their text). */
export function changedLines(before, after) {
  const count = (text) => { const map = new Map(); for (const line of String(text).split('\n')) map.set(line, (map.get(line) || 0) + 1); return map; };
  const a = count(before), b = count(after);
  let different = 0;
  for (const [line, n] of b) different += Math.max(0, n - (a.get(line) || 0));
  for (const [line, n] of a) different += Math.max(0, n - (b.get(line) || 0));
  return different;
}

/** What to do with a stored record when the song is opened: 'none' (nothing stored), 'history' (the same text: only the undo and redo come back) or 'ask' (the browser has other work than the song has, and the server has lost its own working copy). */
export function decide(record, songText, serverKeptWork = false) {
  if (!record) return 'none';
  if (record.text === songText) return 'history';
  return serverKeptWork ? 'none' : 'ask';   // a server that still has unsaved work of its own did not lose anything: the song moved on, an old browser copy is not offered
}
