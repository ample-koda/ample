// A .zip made in the browser: the files of a project in one download, so that people can keep their own copy. Only "stored" entries (no compression): the sounds are compressed already,
// the texts are small. Pure functions (no page), so they are tested in Node.

const TABLE = (() => {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; table[n] = c >>> 0; }
  return table;
})();

/** The CRC-32 of some bytes (what a zip file stores for every entry). */
export function crc32(bytes) {
  let crc = 0xFFFFFFFF;
  for (let i = 0; i < bytes.length; i++) crc = TABLE[(crc ^ bytes[i]) & 0xFF] ^ (crc >>> 8);
  return (crc ^ 0xFFFFFFFF) >>> 0;
}

const u16 = (n) => [n & 255, (n >>> 8) & 255];
const u32 = (n) => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255];

/** The date and time of a Date in the two 16-bit fields of a zip entry (DOS format). */
function dosTime(date) {
  const time = (date.getHours() << 11) | (date.getMinutes() << 5) | (date.getSeconds() >> 1);
  const day = ((Math.max(date.getFullYear(), 1980) - 1980) << 9) | ((date.getMonth() + 1) << 5) | date.getDate();
  return [time & 0xFFFF, day & 0xFFFF];
}

/**
 * The bytes of a zip file. `files` is [{path, data: Uint8Array}] (a path with `/` for folders, UTF-8 names).
 * @returns {Uint8Array}
 */
export function makeZip(files, date = new Date()) {
  const encoder = new TextEncoder();
  const [time, day] = dosTime(date);
  const parts = [];
  const central = [];
  let offset = 0;
  for (const file of files) {
    const name = encoder.encode(file.path);
    const crc = crc32(file.data);
    const header = [...u32(0x04034b50), ...u16(20), ...u16(0x0800), ...u16(0), ...u16(time), ...u16(day), ...u32(crc), ...u32(file.data.length), ...u32(file.data.length), ...u16(name.length), ...u16(0)];
    parts.push(Uint8Array.from(header), name, file.data);
    central.push(Uint8Array.from([...u32(0x02014b50), ...u16(20), ...u16(20), ...u16(0x0800), ...u16(0), ...u16(time), ...u16(day), ...u32(crc), ...u32(file.data.length), ...u32(file.data.length), ...u16(name.length), ...u16(0), ...u16(0), ...u16(0), ...u16(0), ...u32(0), ...u32(offset)]), name);
    offset += header.length + name.length + file.data.length;
  }
  const centralSize = central.reduce((sum, p) => sum + p.length, 0);
  const end = Uint8Array.from([...u32(0x06054b50), ...u16(0), ...u16(0), ...u16(files.length), ...u16(files.length), ...u32(centralSize), ...u32(offset), ...u16(0)]);
  const all = [...parts, ...central, end];
  const out = new Uint8Array(all.reduce((sum, p) => sum + p.length, 0));
  let at = 0;
  for (const p of all) { out.set(p, at); at += p.length; }
  return out;
}

/** The files of a project snapshot ([{path, text}] or [{path, blob}]) as zip entries. */
export async function zipOfSnapshot(files, date = new Date()) {
  const entries = await Promise.all(files.map(async (f) => ({ path: f.path, data: f.blob ? new Uint8Array(await f.blob.arrayBuffer()) : new TextEncoder().encode(f.text) })));
  return makeZip(entries, date);
}
