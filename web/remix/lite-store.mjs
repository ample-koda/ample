// The one place where Remix keeps projects (plan/lite/PLAN.md, D3). The page calls these methods and nothing else; below them is a small key-value layer (lite-kv.mjs): IndexedDB in the
// browser, a Map in the tests. All keys are Remix's own: `meta:app`, `project:<id>`, `file:<id>:<path>`. Reset deletes the whole Remix database and nothing outside it.

import { DEFAULT_LIMITS, checkFile, cleanPath, isText } from './lite-paths.mjs';

export const SCHEMA = 1;
const enc = new TextEncoder();
const FILE = (id, path) => `file:${id}:${path}`;

export class LiteStoreError extends Error {
  constructor(code, message) { super(message); this.code = code; }
}

export function createLiteStore(kv, { now = () => Date.now(), uuid = () => crypto.randomUUID(), limits = DEFAULT_LIMITS } = {}) {
  const bytesOf = (data) => (typeof data === 'string' ? enc.encode(data).length : data.size);
  const normalizeName = (name) => String(name ?? '').trim().slice(0, 60) || 'Untitled';

  async function meta() {
    const found = await kv.get('meta:app');
    if (!found) return { schema: SCHEMA, last: null };
    if (found.schema !== SCHEMA) throw new LiteStoreError('schema', 'The Remix data on this browser was made by another version. Reset the workspace to start fresh.');
    return found;
  }
  const saveMeta = (value) => kv.put('meta:app', value);
  const project = async (id) => {
    const found = await kv.get(`project:${id}`);
    if (!found) throw new LiteStoreError('missing', 'That project does not exist (any more).');
    return found;
  };

  const store = {
    kind: kv.kind,

    /** Reads the settings record; throws a LiteStoreError('schema') when the data is from another version. Safe to call again. */
    async init() {
      const m = await meta();
      if (!(await kv.get('meta:app'))) await saveMeta(m);
      return m;
    },

    async listProjects() {
      const ids = await kv.keys('project:');
      const all = await Promise.all(ids.map((k) => kv.get(k)));
      return all.filter(Boolean).sort((a, b) => b.updated - a.updated);
    },

    async getProject(id) { return project(id); },

    /** Makes a project; `files` is [{path, text}] or [{path, blob, mime}] (a demo copy, an empty song). */
    async createProject({ name, origin = null, files = [] }) {
      const id = uuid();
      const record = { id, name: normalizeName(name), origin, created: now(), updated: now() };
      await kv.put(`project:${id}`, record);
      try {
        for (const file of files) await store.writeFile(id, file.path, file, { touch: false });
      } catch (error) {
        await store.deleteProject(id);
        throw error;
      }
      await store.setLast(id);
      return record;
    },

    async renameProject(id, name) {
      const record = await project(id);
      record.name = normalizeName(name);
      record.updated = now();
      await kv.put(`project:${id}`, record);
      return record;
    },

    async deleteProject(id) {
      for (const key of await kv.keys(FILE(id, ''))) await kv.delete(key);
      await kv.delete(`project:${id}`);
      const m = await meta();
      if (m.last === id) await saveMeta({ ...m, last: null });
    },

    async last() {
      const id = (await meta()).last;
      return id && await kv.get(`project:${id}`) ? id : null;
    },
    async setLast(id) { await saveMeta({ ...(await meta()), last: id }); },

    /** The files of a project, sorted: [{path, kind, size, mime, updated}] (no content). */
    async listFiles(id) {
      const prefix = FILE(id, '');
      const records = await Promise.all((await kv.keys(prefix)).map(async (key) => ({ key, record: await kv.get(key) })));
      return records.filter((r) => r.record).map(({ key, record }) => ({ path: key.slice(prefix.length), kind: record.kind, size: record.size, mime: record.mime ?? '', updated: record.updated }))
        .sort((a, b) => a.path.localeCompare(b.path));
    },

    async usage(id) {
      const files = await store.listFiles(id);
      return { files: files.length, bytes: files.reduce((sum, f) => sum + f.size, 0) };
    },

    /** The content of a file: {kind: 'text', text} or {kind: 'binary', blob, mime}. */
    async readFile(id, path) {
      const found = await kv.get(FILE(id, path));
      if (!found) throw new LiteStoreError('missing', `"${path}" does not exist.`);
      return found.kind === 'text' ? { kind: 'text', text: found.text, size: found.size } : { kind: 'binary', blob: found.blob, mime: found.mime, size: found.size };
    },

    /** Writes a file (text or blob). The name, the type, the size, the number of files and the size of the project are checked first. */
    async writeFile(id, path, content, { touch = true } = {}) {
      await project(id);
      const binary = content.blob !== undefined;
      const data = binary ? content.blob : content.text ?? '';
      const size = bytesOf(data);
      checkFile(path, size, limits);
      if (binary && isText(path)) throw new LiteStoreError('type', `"${path}" is a text file: write text, not audio.`);
      if (!binary && !isText(path)) throw new LiteStoreError('type', `"${path}" is a binary file: give it a Blob.`);
      const existing = await kv.get(FILE(id, path));
      const files = await store.listFiles(id);
      if (!existing && files.length >= limits.maxFiles) throw new LiteStoreError('limit', `A Remix project has at most ${limits.maxFiles} files.`);
      const total = files.reduce((sum, f) => sum + f.size, 0) - (existing?.size ?? 0) + size;
      if (total > limits.maxProjectBytes) throw new LiteStoreError('limit', `The project would be ${Math.ceil(total / 1024 / 1024)} MB; the most is ${Math.floor(limits.maxProjectBytes / 1024 / 1024)} MB.`);
      await kv.put(FILE(id, path), binary ? { kind: 'binary', blob: data, mime: content.mime || data.type || '', size, updated: now() } : { kind: 'text', text: data, size, updated: now() });
      if (touch) await store.touch(id);
    },

    async deleteFile(id, path) {
      await kv.delete(FILE(id, path));
      await store.touch(id);
    },

    /** Deletes a folder: every file under the path. */
    async deleteFolder(id, folder) {
      const prefix = FILE(id, `${cleanPath(folder, limits)}/`);
      for (const key of await kv.keys(prefix)) await kv.delete(key);
      await store.touch(id);
    },

    async renameFile(id, from, to) {
      cleanPath(to, limits);
      if (await kv.get(FILE(id, to))) throw new LiteStoreError('exists', `"${to}" exists already.`);
      const found = await kv.get(FILE(id, from));
      if (!found) throw new LiteStoreError('missing', `"${from}" does not exist.`);
      checkFile(to, found.size, limits);
      await kv.put(FILE(id, to), { ...found, updated: now() });
      await kv.delete(FILE(id, from));
      await store.touch(id);
    },

    /** Remembers which file was open in a project (so a reload comes back to it). Does not count as a change of the project. */
    async setActive(id, path) {
      const record = await kv.get(`project:${id}`);
      if (record && record.active !== path) { record.active = path; await kv.put(`project:${id}`, record); }
    },

    async touch(id) {
      const record = await kv.get(`project:${id}`);
      if (record) { record.updated = now(); await kv.put(`project:${id}`, record); }
    },

    /** Every file with its content, ready to send with a build: [{path, text}] or [{path, blob}]. */
    async snapshot(id) {
      const out = [];
      for (const file of await store.listFiles(id)) {
        const content = await store.readFile(id, file.path);
        out.push(content.kind === 'text' ? { path: file.path, text: content.text } : { path: file.path, blob: content.blob });
      }
      return out;
    },

    /** Deletes all Remix data: the database goes, nothing outside it is touched. */
    async reset() { await kv.destroy(); },
  };
  return store;
}
