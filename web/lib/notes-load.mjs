/** Reads the block notes of one song and returns them; it never touches shared state, so a caller can drop a stale answer. */
export async function readNotes({ get, file, legacyKey, storage }) {
  try {
    const f = await get(`/api/file?path=${encodeURIComponent(file)}&optional=1`);
    if (f.text == null) throw new Error('no notes file yet');
    return { notes: JSON.parse(f.text || '{}'), migrate: false };
  } catch { /* fall through to the notes an older version kept in the browser */ }
  try {
    const old = JSON.parse(storage.getItem(legacyKey) || '{}');
    if (Object.keys(old).length) return { notes: old, migrate: true };
  } catch { /* storage may be blocked */ }
  return { notes: {}, migrate: false };
}
