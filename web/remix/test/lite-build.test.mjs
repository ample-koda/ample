import assert from 'node:assert/strict';
import { test } from 'node:test';
import { buildForm, checkBuild, neededFiles, pickEntry } from '../lite-build.mjs';

const text = (path, body) => ({ path, text: body });
const blob = (path, size = 4) => ({ path, blob: new Blob([new Uint8Array(size)]) });

test('only the audio that the texts mention goes with the build', () => {
  const files = [text('song.amplet', 'drums d { kit samples/kit  pattern kick:x... }\nimport "components/a.amplec"'), text('components/a.amplec', 'x'), blob('samples/kit/kick.wav'), blob('samples/kit/snare.wav'), blob('samples/other/loud.wav'), blob('lead.sf2')];
  const sent = neededFiles(files).map((f) => f.path).sort();
  assert.deepEqual(sent, ['components/a.amplec', 'samples/kit/kick.wav', 'samples/kit/snare.wav', 'song.amplet']);
  const byPath = neededFiles([text('s.amplet', 'file samples/other/loud.wav'), blob('samples/other/loud.wav'), blob('lead.sf2')]).map((f) => f.path).sort();
  assert.deepEqual(byPath, ['s.amplet', 'samples/other/loud.wav']);
});

test('the form holds a manifest and the files in the same order', async () => {
  const form = buildForm('song.amplet', [text('song.amplet', 'hello'), blob('samples/k.wav', 3)]);
  assert.deepEqual(JSON.parse(form.get('manifest')), { entry: 'song.amplet', files: ['song.amplet', 'samples/k.wav'] });
  assert.equal(await form.get('f0').text(), 'hello');
  assert.equal(form.get('f1').size, 3);
});

test('the checks before sending name the problem', () => {
  assert.deepEqual(checkBuild('a.amplet', [text('a.amplet', 'x')]), []);
  assert.match(checkBuild('b.amplet', [text('a.amplet', 'x')])[0], /not in the project/);
  assert.match(checkBuild('a.amplet', [text('a.amplet', 'x'), blob('big.wav', 70 * 1024 * 1024)])[0], /the most is 64 MB/);
});

test('the song to build: the open one, the last built, or the first', () => {
  const paths = ['b.amplet', 'a.amplet', 'c.amplec', 'x.wav'];
  assert.equal(pickEntry(paths, 'b.amplet', null), 'b.amplet');
  assert.equal(pickEntry(paths, 'c.amplec', 'b.amplet'), 'b.amplet');
  assert.equal(pickEntry(paths, 'c.amplec', null), 'a.amplet');
  assert.equal(pickEntry(['c.amplec'], null, null), null);
});

import { buildForm as buildFormWithHashes, sendPlan, sha256Hex, withHashes } from '../lite-build.mjs';

test('a file has the SHA-256 of its content, text as UTF-8 and a Blob as its bytes alike', async () => {
  assert.equal(await sha256Hex('abc'), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  assert.equal(await sha256Hex(new Blob(['abc'])), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
  const [one, two] = await withHashes([{ path: 'a.amplet', text: 'abc' }, { path: 'k.wav', blob: new Blob(['abc']) }]);
  assert.equal(one.sha256, two.sha256);
});

test('a build sends only the files that the server does not have, and lists all of them by hash', async () => {
  const files = await withHashes([{ path: 'song.amplet', text: 'mine' }, { path: 'samples/kit/kick.wav', blob: new Blob(['kick']) }, { path: 'samples/kit/snare.wav', blob: new Blob(['snare']) }]);
  const missing = new Set([files[0].sha256]);   // the server knows the two sounds (a demo) and not my song
  const form = buildFormWithHashes('song.amplet', files, missing);
  const manifest = JSON.parse(form.get('manifest'));
  assert.deepEqual(manifest.files.map((f) => f.path), ['song.amplet', 'samples/kit/kick.wav', 'samples/kit/snare.wav']);
  assert.ok(manifest.files.every((f) => /^[0-9a-f]{64}$/.test(f.sha256)));
  assert.ok(form.has('f0'));
  assert.ok(!form.has('f1') && !form.has('f2'), 'known files are not sent');
  assert.deepEqual(sendPlan(files, missing), [1, 2]);
  // without a list of missing files everything is sent (the server could not be asked)
  const all = buildFormWithHashes('song.amplet', files, null);
  assert.ok(all.has('f0') && all.has('f1') && all.has('f2'));
  assert.deepEqual(sendPlan(files, null), [3, 0]);
});

test('a file without a hash is always sent as a plain path', () => {
  const form = buildFormWithHashes('s.amplet', [{ path: 's.amplet', text: 'x' }], new Set());
  assert.deepEqual(JSON.parse(form.get('manifest')).files, ['s.amplet']);
  assert.ok(form.has('f0'));
});

import { sendSummary, sizeText } from '../lite-build.mjs';

test('the summary of a build says how many files are cached on the server, how many are sent and how big that is', async () => {
  const files = await withHashes([{ path: 'song.amplet', text: 'mine' }, { path: 'k.wav', blob: new Blob([new Uint8Array(2048)]) }, { path: 's.wav', blob: new Blob([new Uint8Array(10)]) }]);
  assert.deepEqual(sendSummary(files, new Set([files[0].sha256])), { total: 3, cached: 2, sending: 1, bytes: 4 });
  assert.deepEqual(sendSummary(files, new Set()), { total: 3, cached: 3, sending: 0, bytes: 0 });
  assert.deepEqual(sendSummary(files, null), { total: 3, cached: 0, sending: 3, bytes: 4 + 2048 + 10 });
  assert.equal(sizeText(900), '900 B');
  assert.equal(sizeText(2048), '2 KB');
  assert.equal(sizeText(3 * 1024 * 1024 + 200 * 1024), '3.2 MB');
});

import { crc32, makeZip, zipOfSnapshot } from '../lite-zip.mjs';

test('a zip made in the browser has the files in it, with the right checksums and names', async () => {
  assert.equal(crc32(new TextEncoder().encode('123456789')), 0xCBF43926);   // the check value of CRC-32
  const zip = await zipOfSnapshot([{ path: 'song.amplet', text: 'hello' }, { path: 'samples/k.wav', blob: new Blob([new Uint8Array([1, 2, 3])]) }], new Date(2026, 9, 30, 12, 0, 0));
  const view = new DataView(zip.buffer);
  assert.equal(view.getUint32(0, true), 0x04034b50);                    // the first local header
  assert.equal(view.getUint32(zip.length - 22, true), 0x06054b50);      // the end record
  assert.equal(view.getUint16(zip.length - 12, true), 2);               // two entries
  const text = new TextDecoder().decode(zip);
  assert.ok(text.includes('song.amplet') && text.includes('samples/k.wav') && text.includes('hello'));
  assert.equal(makeZip([]).length, 22);                                 // an empty zip is the end record only
});
