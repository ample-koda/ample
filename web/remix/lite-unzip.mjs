// Reading a .zip in the browser (a dropped or picked file): the list of its files, and the bytes of one file when it is asked for. "Stored" entries and the usual "deflate" entries are read (the
// browser has DecompressionStream for deflate); a zip that is encrypted, split or zip64 is refused with a clear message. Nothing is trusted: names are cleaned, and the sizes that the zip
// declares are checked against what really comes out, so that a small zip cannot grow into something huge. Pure functions (no page), tested in Node.

const EOCD = 0x06054b50;
const CENTRAL = 0x02014b50;
const LOCAL = 0x04034b50;

/** A zip entry name as a project path: `/` as separator, no leading slash, no `..`, no drive letter. Folders and junk files of other systems give null. */
export function cleanZipPath(name) {
  const path = name.replaceAll('\\', '/').replace(/^\/+/, '');
  if (!path || path.endsWith('/')) return null;
  const parts = path.split('/');
  if (parts.some((p) => p === '..' || p === '.' || p === '') || /^[A-Za-z]:/.test(parts[0])) return null;
  if (parts[0] === '__MACOSX' || parts.at(-1) === '.DS_Store' || parts.at(-1) === 'Thumbs.db' || parts.at(-1).startsWith('._')) return null;
  return parts.join('/');
}

/** When every file of a zip sits in one folder (a zip of a folder), the folder is the project itself: its name goes. */
export function stripCommonRoot(paths) {
  if (paths.length === 0) return paths;
  const first = paths[0].split('/')[0];
  return paths.every((p) => p.includes('/') && p.split('/')[0] === first) ? paths.map((p) => p.slice(first.length + 1)) : paths;
}

/** Reads the bytes of a Blob through a deflate decompressor and stops when more than `limit` bytes come out. */
async function inflate(blob, limit) {
  if (typeof DecompressionStream === 'undefined') throw new Error('This browser cannot open compressed .zip files: use a newer browser, or a zip without compression.');
  const reader = blob.stream().pipeThrough(new DecompressionStream('deflate-raw')).getReader();
  const chunks = [];
  let total = 0;
  for (;;) {
    const { value, done } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > limit) { await reader.cancel(); throw new Error('A file in the .zip is larger than it says.'); }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let at = 0;
  for (const chunk of chunks) { out.set(chunk, at); at += chunk.length; }
  return out;
}

/**
 * The files of a zip: [{path, size, compressed, read()}] where `read()` gives the bytes (checked against the declared size). `entries` also counts what was left out (`skipped`: folders and junk).
 * @param {Blob} file
 */
export async function readZip(file) {
  const size = file.size;
  const tail = new DataView(await file.slice(Math.max(0, size - 65557), size).arrayBuffer());
  let end = -1;
  for (let i = tail.byteLength - 22; i >= 0; i--) if (tail.getUint32(i, true) === EOCD) { end = i; break; }
  if (end < 0) throw new Error('This does not look like a .zip file.');
  const count = tail.getUint16(end + 10, true);
  const centralSize = tail.getUint32(end + 12, true);
  const centralAt = tail.getUint32(end + 16, true);
  if (count === 0xFFFF || centralAt === 0xFFFFFFFF) throw new Error('This .zip is too big or too special (zip64): Remix cannot read it.');
  if (centralAt + centralSize > size) throw new Error('This .zip is damaged.');
  const view = new DataView(await file.slice(centralAt, centralAt + centralSize).arrayBuffer());
  const decoder = new TextDecoder('utf-8');
  const entries = [];
  let skipped = 0;
  let at = 0;
  for (let n = 0; n < count; n++) {
    if (at + 46 > view.byteLength || view.getUint32(at, true) !== CENTRAL) throw new Error('This .zip is damaged.');
    const flags = view.getUint16(at + 8, true);
    const method = view.getUint16(at + 10, true);
    const compressed = view.getUint32(at + 20, true);
    const length = view.getUint32(at + 24, true);
    const nameLength = view.getUint16(at + 28, true);
    const extraLength = view.getUint16(at + 30, true);
    const commentLength = view.getUint16(at + 32, true);
    const offset = view.getUint32(at + 42, true);
    const name = decoder.decode(new Uint8Array(view.buffer, at + 46, nameLength));
    at += 46 + nameLength + extraLength + commentLength;
    const path = cleanZipPath(name);
    if (path === null) { skipped++; continue; }
    if (flags & 1) throw new Error('This .zip is protected with a password: Remix cannot open it.');
    if (method !== 0 && method !== 8) throw new Error(`"${path}" is packed in a way that Remix cannot read (method ${method}).`);
    entries.push({
      path,
      size: length,
      compressed,
      async read() {
        const local = new DataView(await file.slice(offset, offset + 30).arrayBuffer());
        if (local.getUint32(0, true) !== LOCAL) throw new Error('This .zip is damaged.');
        const start = offset + 30 + local.getUint16(26, true) + local.getUint16(28, true);
        const packed = file.slice(start, start + compressed);
        const bytes = method === 0 ? new Uint8Array(await packed.arrayBuffer()) : await inflate(packed, length);
        if (bytes.length !== length) throw new Error(`"${path}" does not have the size that the .zip says.`);
        return bytes;
      },
    });
  }

  return { entries, skipped };
}
