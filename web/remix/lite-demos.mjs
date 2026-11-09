// The built-in demos of Remix (plan/lite/PLAN.md, D11 and D12). They are read-only templates served as static files: web/remix/demos/<id>/<path> for the files of a demo and
// web/remix/assets/<pack>/<file> for the sounds that demos share. "Using" a demo copies its files into a new local project; nothing ever writes back to a demo.

import { isText } from './lite-paths.mjs';

export const CATEGORIES = ['Basics', 'Instruments', 'Effects', 'Components', 'Genres', 'Advanced'];
export const LEVELS = ['Beginner', 'Intermediate', 'Advanced'];

/** The catalog: [{id, name, description, category, level, tags, files, assets}]. `assets` is [{pack, to, files}] (the files of a pack that go to a folder of the project). */
export async function loadCatalog(fetchFn = fetch) {
  const response = await fetchFn('/remix/demos/catalog.json', { cache: 'no-cache' });
  if (!response.ok) throw new Error('The demos could not be loaded.');
  const demos = await response.json();
  return demos.map((d) => ({ tags: [], files: [], assets: [], level: 'Beginner', ...d }));
}

/** Fetches every file of a demo (its own files and the assets it names) as [{path, text}] or [{path, blob}]. */
export async function demoFiles(demo, fetchFn = fetch, { concurrency = 8, wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms)) } = {}) {
  // the files of the demo itself are asked again every time (a demo can change in a release; the browser may still hold an older answer), the sounds keep the cache they are given.
  // A demo can hold over a hundred files: asked all at once, the server's limit on simultaneous requests of one visitor answers 429 to the surplus (the demo then seemed to lack a file).
  // So a few at a time, and a 429 or 503 is asked again after a short wait.
  const get = async (url, path, options = undefined) => {
    let response;
    for (let attempt = 0; attempt < 5; attempt++) {
      // a 429 that came earlier may sit in the browser cache for an hour (the server marks every answer of the sounds cacheable): the second try goes around the cache
      response = await fetchFn(url, attempt === 0 ? options : { ...(options || {}), cache: 'reload' });
      if (response.ok || (response.status !== 429 && response.status !== 503)) break;
      await wait(300 * (attempt + 1));
    }
    if (!response.ok) throw new Error(`The demo "${demo.name}" is missing "${path}".`);
    return isText(path) ? { path, text: await response.text() } : { path, blob: await response.blob() };
  };
  const jobs = [
    ...demo.files.map((path) => () => get(`/remix/demos/${demo.id}/${path}`, path, { cache: 'no-cache' })),
    ...demo.assets.flatMap((a) => a.files.map((file) => () => get(`/remix/assets/${a.pack}/${file}`, `${a.to}/${file}`))),
  ];
  const results = new Array(jobs.length);
  let next = 0;
  const worker = async () => { while (next < jobs.length) { const at = next++; results[at] = await jobs[at](); } };
  await Promise.all(Array.from({ length: Math.max(1, Math.min(concurrency, jobs.length)) }, worker));
  return results;
}

/** The demos that match a search text and a category (empty means all); the text looks in the name, description, tags and level. */
export function filterDemos(demos, { query = '', category = '' } = {}) {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean);
  return demos.filter((d) => (!category || d.category === category) && words.every((w) => `${d.name} ${d.description} ${d.tags.join(' ')} ${d.level} ${d.category}`.toLowerCase().includes(w)));
}

/** The song of a demo: the one named `entry`, or the first .amplet. */
export const entryOf = (demo) => demo.entry ?? demo.files.find((f) => f.endsWith('.amplet')) ?? null;

/** The id of the demo that a link asks for (`#demo=mozart`), or null. */
export function sharedDemoId(hash) {
  const match = /^#demo=([A-Za-z0-9._-]{1,64})$/.exec(hash ?? '');
  return match ? match[1] : null;
}

/** The link to a demo on this page: the address without any hash, then `#demo=<id>`. */
export const shareUrl = (location, demo) => `${location.origin}${location.pathname}#demo=${encodeURIComponent(demo.id)}`;

/**
 * True when the files of a project are exactly the files of its demo (same names, same text, same size for sounds). Only an unchanged copy may be shared:
 * the link opens the demo as it is on the server, so it would not be what the sharer is looking at.
 * `kept` is [{path, kind, size}] from the store, `read(path)` gives {kind, text|size} of one of them, `files` is what demoFiles returned.
 */
export async function isUnchangedDemo(kept, read, files) {
  if (kept.length !== files.length) return false;
  const byPath = new Map(kept.map((f) => [f.path, f]));
  for (const file of files) {
    const stored = byPath.get(file.path);
    if (!stored) return false;
    if (file.text !== undefined) {
      if (stored.kind !== 'text' || (await read(file.path)).text !== file.text) return false;
    } else if (stored.kind === 'text' || stored.size !== file.blob.size) return false;
  }
  return true;
}
