// What Remix sends to the server for a build, and what it gets back. Pure functions (no page, no network), so they are tested in Node.

import { DEFAULT_LIMITS, isText } from './lite-paths.mjs';

/**
 * The files a build needs: every text file (sources and components are small) and the audio, SoundFont and MIDI files that the texts mention, by their path or by a folder that
 * holds them (`kit samples/kit` takes the files of that folder). A file that nothing mentions stays at home. `files` is a snapshot: [{path, text}] or [{path, blob}].
 */
export function neededFiles(files) {
  const texts = files.filter((f) => f.text !== undefined);
  const haystack = texts.map((f) => f.text).join('\n');
  const mentioned = (path) => {
    const parts = path.split('/');
    for (let n = parts.length; n >= 1; n--) {
      const prefix = parts.slice(0, n).join('/');
      if (prefix !== 'samples' && haystack.includes(prefix)) return true;   // "samples" alone would take every sample of the project
    }
    return false;
  };
  return files.filter((f) => f.text !== undefined || mentioned(f.path));
}

/** The SHA-256 of a file's content (a Blob or a text, as UTF-8) as 64 lower case hexadecimal characters; null when this browser has no WebCrypto (a plain http page). */
export async function sha256Hex(data) {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) return null;
  const bytes = typeof data === 'string' ? new TextEncoder().encode(data) : new Uint8Array(await data.arrayBuffer());
  return [...new Uint8Array(await subtle.digest('SHA-256', bytes))].map((v) => v.toString(16).padStart(2, '0')).join('');
}

/** The files with their hash: [{path, text|blob, sha256}]. When the hash cannot be made, the files come back as they are (the build then sends everything). */
export async function withHashes(files) {
  const hashed = await Promise.all(files.map(async (f) => ({ ...f, sha256: await sha256Hex(f.blob ?? f.text) })));
  return hashed.every((f) => f.sha256) ? hashed : files;
}

/**
 * The multipart body of a build: a manifest (the song and the files) and one part per file that has to be sent, named f0, f1, ... by the place of the file in the manifest.
 * A file with a hash is listed as {path, sha256}; when `missing` is a Set of hashes, only the files whose hash is in it are sent (the server has the others: the files of the
 * demos, and what an earlier build sent in the last hour). Without `missing` every file is sent.
 */
export function buildForm(entry, files, missing = null) {
  const form = new FormData();
  form.append('manifest', JSON.stringify({ entry, files: files.map((f) => (f.sha256 ? { path: f.path, sha256: f.sha256 } : f.path)) }));
  files.forEach((f, i) => {
    if (missing && f.sha256 && !missing.has(f.sha256)) return;
    form.append(`f${i}`, f.blob ?? new Blob([f.text], { type: 'text/plain' }), 'file');
  });
  return form;
}

/** How many of the files a build has to send: [sent, known]. */
export const sendPlan = (files, missing) => {
  const sent = files.filter((f) => !missing || !f.sha256 || missing.has(f.sha256)).length;
  return [sent, files.length - sent];
};

/** Checks the whole thing against the limits before it is sent; returns the problems as strings (an empty list means go ahead). */
export function checkBuild(entry, files, limits = DEFAULT_LIMITS) {
  const problems = [];
  if (!files.some((f) => f.path === entry)) problems.push(`The song "${entry}" is not in the project.`);
  if (files.length > limits.maxFiles) problems.push(`A Remix project has at most ${limits.maxFiles} files.`);
  const total = files.reduce((sum, f) => sum + (f.blob ? f.blob.size : new TextEncoder().encode(f.text).length), 0);
  if (total > limits.maxProjectBytes) problems.push(`The files of this build are ${Math.ceil(total / 1024 / 1024)} MB; the most is ${Math.floor(limits.maxProjectBytes / 1024 / 1024)} MB.`);
  return problems;
}

/** The song to build: the one that is open, else the one that was built last, else the first song of the project. */
export function pickEntry(paths, active, lastBuilt) {
  const songs = paths.filter((p) => p.endsWith('.amplet')).sort();
  if (active && songs.includes(active)) return active;
  if (lastBuilt && songs.includes(lastBuilt)) return lastBuilt;
  return songs[0] ?? null;
}

/** The text for a build problem: where it is and what. */
export const describe = (d) => `${d.severity === 'error' ? 'Error' : d.severity === 'warning' ? 'Warning' : 'Note'} ${d.code}${d.line > 0 ? ` (line ${d.line})` : ''}: ${d.message}`;

/** True for the files that only matter as sound: they have no editor, only a preview. */
export const isBinary = (path) => !isText(path);

/** What a build does about its files: how many there are, how many the server has already (cached), how many are sent and how many bytes that is. */
export function sendSummary(files, missing) {
  const size = (f) => (f.blob ? f.blob.size : new TextEncoder().encode(f.text).length);
  const sending = files.filter((f) => !missing || !f.sha256 || missing.has(f.sha256));
  return { total: files.length, cached: files.length - sending.length, sending: sending.length, bytes: sending.reduce((sum, f) => sum + size(f), 0) };
}

/** A size for people: bytes, KB or MB. */
export const sizeText = (bytes) => (bytes < 1024 ? `${bytes} B` : bytes < 1048576 ? `${Math.round(bytes / 1024)} KB` : `${(bytes / 1048576).toFixed(1)} MB`);
