// One memory budget for decoded takes (plan local-preview 1.3). Entries are AudioBuffer-like objects
// ({ length, numberOfChannels }); the cache counts length * channels * 4 bytes, evicts the least recently
// used unpinned entry when the budget is exceeded, and never evicts a pinned entry (a live chain uses it).
const MIB = 1024 * 1024;

export function createTakeCache({ budgetMb = 512 } = {}) {
  let budget = budgetMb * MIB;
  const entries = new Map();   // key -> { promise, buffer, bytes, pins }; Map order = recency (oldest first)
  const bytesOf = (buffer) => buffer.length * buffer.numberOfChannels * 4;
  const total = () => { let sum = 0; for (const e of entries.values()) sum += e.bytes; return sum; };
  const keyOf = (takeId, range) => range ? `${takeId}@${range.start}-${range.end}` : String(takeId);
  const evict = () => {
    for (const [key, entry] of entries) {
      if (total() <= budget) return;
      if (entry.pins === 0 && entry.buffer) entries.delete(key);
    }
  };
  return {
    /** load(): () => Promise<AudioBuffer>; a second call for the same key shares the first promise. */
    async getDecoded(takeId, load, { range } = {}) {
      const key = keyOf(takeId, range);
      let entry = entries.get(key);
      if (entry) { entries.delete(key); entries.set(key, entry); return entry.promise; }   // touch: most recent
      entry = { promise: null, buffer: null, bytes: 0, pins: 0 };
      entry.promise = Promise.resolve().then(load).then((buffer) => { entry.buffer = buffer; entry.bytes = bytesOf(buffer); evict(); return buffer; },
        (error) => { if (entries.get(key) === entry) entries.delete(key); throw error; });
      entries.set(key, entry);
      return entry.promise;
    },
    pin(takeId, range) { const e = entries.get(keyOf(takeId, range)); if (e) e.pins++; return Boolean(e); },
    unpin(takeId, range) { const e = entries.get(keyOf(takeId, range)); if (e && e.pins > 0) { e.pins--; evict(); } },
    setBudget(mb) { budget = mb * MIB; evict(); },
    has: (takeId, range) => entries.has(keyOf(takeId, range)),
    bytes: total,
    size: () => entries.size,
  };
}

/** The app-wide cache. */
export const takeCache = createTakeCache();
