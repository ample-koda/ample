// The two key-value layers under the Remix store. Both have the same small interface:
//   get(key) -> value | undefined, put(key, value), delete(key), keys(prefix) -> string[], destroy()
// `memoryKv` is for tests; `idbKv` keeps the values (text, Blobs, small records) in one IndexedDB database. Nothing else in the page touches IndexedDB.

// the name of the store of this instance (a setting of the server: AMPLE_REMIX_STORE, sent with the limits); the first public instance keeps its old name (ample-lite) so that nobody loses a song there
export let DATABASE = 'ample-remix';
export const setStoreName = (name) => { if (name) DATABASE = name; };
export const storeName = () => DATABASE;

export function memoryKv() {
  const map = new Map();
  return {
    kind: 'memory',
    async get(key) { return map.has(key) ? structuredClone(map.get(key)) : undefined; },
    async put(key, value) { map.set(key, structuredClone(value)); },
    async delete(key) { map.delete(key); },
    async keys(prefix = '') { return [...map.keys()].filter((k) => k.startsWith(prefix)).sort(); },
    async destroy() { map.clear(); },
  };
}

/** IndexedDB, database "ample-lite", one object store "kv". `idb` is `indexedDB` (a parameter so that a test can give another). */
export function idbKv(idb = globalThis.indexedDB, name = DATABASE, { timeoutMs = 8000 } = {}) {
  if (!idb) throw new Error('This browser has no IndexedDB (a private window may block it).');
  let opened = null;
  const open = () => opened ??= new Promise((resolve, reject) => {
    const request = idb.open(name, 1);
    request.onupgradeneeded = () => { if (!request.result.objectStoreNames.contains('kv')) request.result.createObjectStore('kv'); };
    request.onsuccess = () => { request.result.onversionchange = () => request.result.close(); resolve(request.result); };
    request.onerror = () => { opened = null; reject(request.error || new Error('The Remix database could not be opened.')); };
    request.onblocked = () => { opened = null; reject(new Error('The Remix database is open in another tab that has to close first.')); };
  });
  // A browser can leave a transaction waiting for ever (another tab that was frozen in the middle of a write, or an in-app browser): after a while it is an error the page can show.
  const STUCK = 'The browser storage does not answer (another tab of Remix may be holding it, or this in-app browser blocks it): close other Remix tabs, or open the link in your normal browser, and try again.';
  const waiting = (promise) => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(STUCK)), timeoutMs);
    promise.then((v) => { clearTimeout(timer); resolve(v); }, (e) => { clearTimeout(timer); reject(e); });
  });
  const run = async (mode, work) => {
    const db = await waiting(open());
    return waiting(new Promise((resolve, reject) => {
      const tx = db.transaction('kv', mode);
      let result;
      Promise.resolve(work(tx.objectStore('kv'))).then((value) => { result = value; }).catch(reject);
      tx.oncomplete = () => resolve(result);
      tx.onerror = () => reject(tx.error);
      tx.onabort = () => reject(tx.error || new Error('The Remix database refused the change.'));
    }));
  };
  const wrap = (request) => new Promise((resolve, reject) => { request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error); });
  return {
    kind: 'indexeddb',
    get: (key) => run('readonly', (store) => wrap(store.get(key))),
    put: (key, value) => run('readwrite', (store) => wrap(store.put(value, key))),
    delete: (key) => run('readwrite', (store) => wrap(store.delete(key))),
    keys: (prefix = '') => run('readonly', async (store) => {
      const all = await wrap(store.getAllKeys(prefix ? IDBKeyRange.bound(prefix, `${prefix}￿`) : undefined));
      return all.map(String).sort();
    }),
    /** Deletes the database (only the Remix one) and gives a fresh start. */
    async destroy() {
      if (opened) { try { (await opened).close(); } catch { /* it did not open */ } opened = null; }
      await new Promise((resolve, reject) => {
        const request = idb.deleteDatabase(name);
        request.onsuccess = () => resolve();
        request.onerror = () => reject(request.error);
        request.onblocked = () => reject(new Error('The Remix database is open in another tab that has to close first.'));
      });
    },
  };
}
