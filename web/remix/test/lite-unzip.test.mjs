import assert from 'node:assert/strict';
import { test } from 'node:test';
import { deflateRawSync } from 'node:zlib';
import { cleanZipPath, readZip, stripCommonRoot } from '../lite-unzip.mjs';
import { makeZip } from '../lite-zip.mjs';

const bytes = (text) => new TextEncoder().encode(text);

/** A zip with "deflate" entries, made by hand (the zip maker of Lite only stores). `lie` makes an entry say that it is smaller than it is. */
function deflatedZip(files, { lie = 0, flags = 0 } = {}) {
  const parts = [], central = [];
  let offset = 0;
  const u16 = (n) => [n & 255, (n >>> 8) & 255];
  const u32 = (n) => [n & 255, (n >>> 8) & 255, (n >>> 16) & 255, (n >>> 24) & 255];
  for (const { path, data } of files) {
    const name = bytes(path), packed = deflateRawSync(data), declared = Math.max(0, data.length - lie);
    const local = [...u32(0x04034b50), ...u16(20), ...u16(flags), ...u16(8), ...u16(0), ...u16(0), ...u32(0), ...u32(packed.length), ...u32(declared), ...u16(name.length), ...u16(0)];
    parts.push(Uint8Array.from(local), name, packed);
    central.push(Uint8Array.from([...u32(0x02014b50), ...u16(20), ...u16(20), ...u16(flags), ...u16(8), ...u16(0), ...u16(0), ...u32(0), ...u32(packed.length), ...u32(declared), ...u16(name.length), ...u16(0), ...u16(0), ...u16(0), ...u16(0), ...u32(0), ...u32(offset)]), name);
    offset += local.length + name.length + packed.length;
  }
  const size = central.reduce((sum, p) => sum + p.length, 0);
  const end = Uint8Array.from([...u32(0x06054b50), ...u16(0), ...u16(0), ...u16(files.length), ...u16(files.length), ...u32(size), ...u32(offset), ...u16(0)]);
  return new Blob([...parts, ...central, end]);
}

test('the names of a zip become safe project paths, and junk is left out', () => {
  assert.equal(cleanZipPath('song.amplet'), 'song.amplet');
  assert.equal(cleanZipPath('samples\\kit\\kick.wav'), 'samples/kit/kick.wav');
  assert.equal(cleanZipPath('/etc/passwd'), 'etc/passwd');
  for (const bad of ['../x.wav', 'a/../../x.wav', 'C:/x.wav', 'folder/', '__MACOSX/a.wav', 'a/.DS_Store', 'a/._b.wav', 'a//b.wav', '']) assert.equal(cleanZipPath(bad), null, bad);
  assert.deepEqual(stripCommonRoot(['my song/song.amplet', 'my song/samples/k.wav']), ['song.amplet', 'samples/k.wav']);
  assert.deepEqual(stripCommonRoot(['song.amplet', 'samples/k.wav']), ['song.amplet', 'samples/k.wav']);
  assert.deepEqual(stripCommonRoot(['a/x.wav', 'b/y.wav']), ['a/x.wav', 'b/y.wav']);
});

test('a stored zip (the one that Lite makes) is read back, file by file', async () => {
  const zip = new Blob([makeZip([{ path: 'song.amplet', data: bytes('hello') }, { path: 'samples/k.wav', data: Uint8Array.from([1, 2, 3, 4]) }])]);
  const { entries, skipped } = await readZip(zip);
  assert.deepEqual(entries.map((e) => [e.path, e.size]), [['song.amplet', 5], ['samples/k.wav', 4]]);
  assert.equal(skipped, 0);
  assert.equal(new TextDecoder().decode(await entries[0].read()), 'hello');
  assert.deepEqual([...await entries[1].read()], [1, 2, 3, 4]);
});

test('a zip with compression is read too, and a folder entry and junk are counted as skipped', async () => {
  const text = 'composition "x" { ample 1  tempo 120 }\n'.repeat(200);
  const zip = deflatedZip([{ path: 'song.amplet', data: bytes(text) }, { path: '__MACOSX/._song.amplet', data: bytes('junk') }, { path: 'dir/', data: new Uint8Array(0) }]);
  const { entries, skipped } = await readZip(zip);
  assert.equal(entries.length, 1);
  assert.equal(skipped, 2);
  assert.equal(entries[0].compressed < entries[0].size, true);
  assert.equal(new TextDecoder().decode(await entries[0].read()), text);
});

test('a file that is bigger than the zip says is refused, and so is an encrypted or broken zip', async () => {
  const lying = await readZip(deflatedZip([{ path: 'a.txt', data: bytes('x'.repeat(5000)) }], { lie: 4000 }));
  await assert.rejects(() => lying.entries[0].read(), /larger than it says|does not have the size/);
  await assert.rejects(() => readZip(deflatedZip([{ path: 'a.txt', data: bytes('x') }], { flags: 1 })), /password/);
  await assert.rejects(() => readZip(new Blob([bytes('this is not a zip file at all, it is just text')])), /does not look like a \.zip/);
  await assert.rejects(() => readZip(new Blob([])), /does not look like a \.zip/);
});
