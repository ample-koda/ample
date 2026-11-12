// Rendered SoundFont notes kept in the browser (plan local-preview 3.7): a GM note is rendered once by the server (/api/gm/audition) and the WAV is stored in IndexedDB under the
// SoundFont, bank, program, pitch, velocity step and length, so the same note later (another session, another song) plays at once without a round trip. Only packs (`pack:name`)
// are kept: a pack does not change under its name, a project SoundFont file may, and nothing here can see that. The cache stays in this browser (a few samples of a licensed
// SoundFont are never uploaded anywhere) and has a byte budget; the least recently used samples go first. Everything is wrapped so a blocked IndexedDB just means no cache.

export const SF_CACHE_BYTES = 96 * 1024 * 1024;
const velocityStep = (velocity) => Math.max(1, Math.min(127, Math.round((Number(velocity) || 100) / 32) * 32 || 32));   // four velocity layers

/** Whether a SoundFont can be cached at all: a pack by its name, a project file by its size and modification time (see cacheFile). */
export const cacheable = (file) => /^pack:/.test(String(file || '')) || String(file || '').trim().length > 0;

/**
 * The name a SoundFont is cached under: a pack is its own name, a project file gets its size and modification time added (read from the listing of its folder), so an edited file
 * never plays old samples. Returns null when the signature cannot be read (then nothing is cached). `list(folder)` gives the children of a project folder: [{ name, sizeBytes, modified }].
 */
export async function cacheFile(file, list) {
  const text = String(file || '');
  if (/^pack:/.test(text)) return text;
  try {
    const cut = text.lastIndexOf('/'), folder = cut < 0 ? '' : text.slice(0, cut), name = text.slice(cut + 1);
    const child = (await list(folder)).find((c) => c.name === name);
    return child && Number.isFinite(child.sizeBytes) ? `${text}@${child.sizeBytes}:${child.modified ?? ''}` : null;
  } catch { return null; }
}

/** The key of one rendered note. `ticks` is the length in millionths of a quarter at 120 bpm (what the audition endpoint takes), snapped to 50 ms steps so nearby lengths share one sample. */
export function sampleKey({ file, bank = 0, program = 0, channel = 0, drum = false, pitch, velocity = 100, ticks = 0 }) {
  const stepped = ticks ? Math.max(1, Math.round(ticks / 50_000)) * 50_000 : 0;
  return [String(file), drum ? 'kit' : `${bank}:${program}`, channel, pitch, velocityStep(velocity), stepped].join('|');
}

/** The velocity the note is rendered with when it is cached: the step it was snapped to. */
export const cachedVelocity = velocityStep;

/** In-memory storage with the shape of the IndexedDB one (used by tests and when IndexedDB is not there). */
export function memoryStore() {
  const map = new Map();
  return { async get(key) { return map.get(key) ?? null; }, async put(key, record) { map.set(key, record); }, async delete(key) { map.delete(key); }, async all() { return [...map].map(([key, r]) => ({ key, bytes: r.bytes, used: r.used })); }, async clear() { map.clear(); } };
}

/** IndexedDB storage, or null when it is not available (private window, blocked site data). */
export async function indexedStore(factory = globalThis.indexedDB, name = 'ample-sf-samples') {
  if (!factory) return null;
  const db = await new Promise((resolve) => {
    try {
      const request = factory.open(name, 1);
      request.onupgradeneeded = () => request.result.createObjectStore('samples');
      request.onsuccess = () => resolve(request.result);
      request.onerror = () => resolve(null);
      request.onblocked = () => resolve(null);
    } catch { resolve(null); }
  });
  if (!db) return null;
  const run = (mode, work) => new Promise((resolve) => {
    try {
      const transaction = db.transaction('samples', mode), store = transaction.objectStore('samples');
      let result = null;
      work(store, (value) => { result = value; });
      transaction.oncomplete = () => resolve(result);
      transaction.onerror = transaction.onabort = () => resolve(null);
    } catch { resolve(null); }
  });
  return {
    get: (key) => run('readonly', (store, out) => { const r = store.get(key); r.onsuccess = () => out(r.result ?? null); }),
    put: (key, record) => run('readwrite', (store) => { store.put(record, key); }),
    delete: (key) => run('readwrite', (store) => { store.delete(key); }),
    all: () => run('readonly', (store, out) => { const rows = []; const r = store.openCursor(); r.onsuccess = () => { const cursor = r.result; if (cursor) { rows.push({ key: cursor.key, bytes: cursor.value.bytes, used: cursor.value.used }); cursor.continue(); } else out(rows); }; }).then((rows) => rows || []),
    clear: () => run('readwrite', (store) => { store.clear(); }),
  };
}

/** The cache over a store: get(key) -> ArrayBuffer | null, put(key, ArrayBuffer), trim(), clear(). */
export function createSampleCache(store, { maxBytes = SF_CACHE_BYTES, now = () => Date.now() } = {}) {
  let total = null;
  const measure = async () => { if (total === null) total = (await store.all()).reduce((sum, row) => sum + (row.bytes || 0), 0); return total; };
  async function trim() {
    let used = await measure();
    if (used <= maxBytes) return;
    const rows = (await store.all()).sort((a, b) => (a.used || 0) - (b.used || 0));
    for (const row of rows) {
      if (used <= maxBytes * 0.8) break;   // down to 80 percent, so every put does not trim again
      await store.delete(row.key); used -= row.bytes || 0;
    }
    total = used;
  }
  return {
    async get(key) {
      if (!store) return null;
      const record = await store.get(key);
      if (!record) return null;
      store.put(key, { ...record, used: now() });   // a hit makes it recent
      return record.data;
    },
    async put(key, data) {
      if (!store || !data?.byteLength || data.byteLength > maxBytes / 4) return false;
      await measure();
      const old = await store.get(key);
      await store.put(key, { data, bytes: data.byteLength, used: now() });
      total += data.byteLength - (old?.bytes || 0);
      await trim();
      return true;
    },
    async clear() { if (store) await store.clear(); total = 0; },
    bytes: measure,
  };
}

let shared = null;
/** The cache of this page (IndexedDB when there is one, otherwise memory). */
export function sharedSampleCache() {
  if (!shared) shared = indexedStore().then((store) => createSampleCache(store || memoryStore())).catch(() => createSampleCache(memoryStore()));
  return shared;
}
