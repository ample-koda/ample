import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Adding files to a Lite project: dropped on the page, picked with Upload (a phone has no drag and drop), a .zip that asks what to do, Cancel that puts everything back, and the export as a .zip.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/#token=');
const wipe = async (page: Page) => page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await wipe(page);
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}
async function useDemo(page: Page, id: string) {
  await page.click('#lite-demos-btn');
  await page.locator(`#lite-demo-list li[data-demo="${id}"] button`).click();
  await expect(page.locator('#lite-demo-dialog')).toBeHidden();
  await expect(page.locator('#lite-filename')).not.toHaveText('');
}
const paths = (page: Page) => page.locator('#lite-tree .lite-row.file').evaluateAll((rows) => rows.map((r) => (r as HTMLElement).dataset.path ?? r.textContent ?? ''));

/** Drops files on the page the way a browser does: dragenter, then drop, with a DataTransfer that holds the files. */
async function drop(page: Page, files: Array<{ name: string; text?: string; bytes?: number[]; zip?: Array<{ path: string; text: string }> }>) {
  await page.evaluate(async (list) => {
    const { makeZip } = await import('/remix/lite-zip.mjs');
    const dt = new DataTransfer();
    for (const f of list) {
      const data = f.zip ? makeZip(f.zip.map((e) => ({ path: e.path, data: new TextEncoder().encode(e.text) }))) : f.bytes ? Uint8Array.from(f.bytes) : new TextEncoder().encode(f.text ?? '');
      dt.items.add(new File([data], f.name));
    }
    document.dispatchEvent(new DragEvent('dragenter', { dataTransfer: dt, bubbles: true }));
    document.dispatchEvent(new DragEvent('drop', { dataTransfer: dt, bubbles: true, cancelable: true }));
  }, files);
}

test('a sound dropped on the page is added to the project, in the samples folder, and the veil shows while dragging', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await page.evaluate(() => { const dt = new DataTransfer(); dt.items.add(new File(['x'], 'a.wav')); document.dispatchEvent(new DragEvent('dragenter', { dataTransfer: dt, bubbles: true })); });
  await expect(page.locator('#lite-drop')).toBeVisible();
  await drop(page, [{ name: 'kick drum.wav', bytes: [82, 73, 70, 70, 0, 0, 0, 0] }, { name: 'notes.txt', text: 'hello' }]);
  await expect(page.locator('#lite-drop')).toBeHidden();
  await expect(page.locator('#lite-toasts')).toContainText('2 files added');
  const list = await paths(page);
  expect(list).toContain('samples/kick drum.wav');
  expect(list).toContain('notes.txt');
  await expect(page.locator('#lite-import-dialog')).toBeHidden();
});

test('Upload (the way on a phone) takes a file too, and a second one with the same name gets a number', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const wav = { name: 'snare.wav', mimeType: 'audio/wav', buffer: Buffer.from('RIFF....') };
  await page.setInputFiles('#lite-upload', wav);
  await expect(page.locator('#lite-toasts')).toContainText('1 file added');
  await expect.poll(() => paths(page)).toContain('samples/snare.wav');   // the first one is in before the second comes
  await page.setInputFiles('#lite-upload', wav);
  await expect(page.locator('#lite-toasts')).toContainText('1 file added');
  const list = await paths(page);
  expect(list).toContain('samples/snare.wav');
  await expect.poll(() => paths(page)).toContain('samples/snare-2.wav');
});

test('a .zip asks what to do: open it as a project of its own (the folder it sits in is not part of the paths)', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await drop(page, [{ name: 'Mijn Song.zip', zip: [{ path: 'Mijn Song/song.amplet', text: 'composition "mijn" { ample 1  tempo 120  master-seed 1 }\n' }, { path: 'Mijn Song/samples/k.wav', text: 'RIFF' }, { path: '__MACOSX/Mijn Song/._song.amplet', text: 'junk' }] }]);
  await expect(page.locator('#lite-zip-dialog')).toBeVisible();
  await expect(page.locator('#lite-zip-name')).toHaveText('Mijn Song.zip');
  await expect(page.locator('#lite-zip-summary')).toContainText('2 files');
  await page.check('#lite-zip-dialog input[value="project"]');
  await page.click('#lite-zip-go');
  await expect(page.locator('#lite-zip-dialog')).toBeHidden();
  await expect(page.locator('#lite-projects option:checked')).toHaveText('Mijn Song');
  const list = await paths(page);
  expect(list).toEqual(expect.arrayContaining(['song.amplet', 'samples/k.wav']));
  expect(list.some((p) => p.includes('MACOSX'))).toBe(false);
});

test('a .zip can add its files to the open project: names that are taken get a number', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  await drop(page, [{ name: 'extra.zip', zip: [{ path: 'song.amplet', text: 'composition "other" { ample 1  tempo 90  master-seed 2 }\n' }, { path: 'components/c.amplec', text: '// c' }] }]);
  await expect(page.locator('#lite-zip-dialog')).toBeVisible();
  await expect(page.locator('#lite-zip-dialog input[value="add"]')).toBeChecked();   // the first choice when there is an open project
  await page.click('#lite-zip-go');
  await expect(page.locator('#lite-toasts')).toContainText('2 files of extra.zip added');
  const list = await paths(page);
  expect(list).toEqual(expect.arrayContaining(['song.amplet', 'song-2.amplet', 'components/c.amplec']));
});

test('Cancel in the .zip window changes nothing, and a Cancel while files are added puts the project back as it was', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const before = await paths(page);
  await drop(page, [{ name: 'a.zip', zip: [{ path: 'x.txt', text: 'x' }] }]);
  await page.click('#lite-zip-cancel');
  await expect(page.locator('#lite-zip-dialog')).toBeHidden();
  expect(await paths(page)).toEqual(before);
  // slow down the reading of text files so that Cancel can be pressed while files are being added
  await page.evaluate(() => { const original = Blob.prototype.text; Blob.prototype.text = async function slow() { await new Promise((resolve) => setTimeout(resolve, 400)); return original.call(this); }; });
  await drop(page, [1, 2, 3, 4, 5, 6].map((n) => ({ name: `n${n}.txt`, text: `note ${n}` })));
  await expect(page.locator('#lite-import-dialog')).toBeVisible();
  await expect(page.locator('#lite-import-line')).toContainText('of 6');
  await page.waitForTimeout(900);
  await page.click('#lite-import-cancel');
  await expect(page.locator('#lite-import-dialog')).toBeHidden({ timeout: 15000 });
  await expect(page.locator('#lite-toasts')).toContainText('Cancelled: nothing was added');
  expect(await paths(page)).toEqual(before);   // what was added before the Cancel is removed again
});

test('a .zip that is not a zip, and a file Lite cannot use, are told about and change nothing', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'hello-melody');
  const before = await paths(page);
  await drop(page, [{ name: 'broken.zip', text: 'this is not a zip' }]);
  await expect(page.locator('#lite-toasts')).toContainText('does not look like a .zip');
  await drop(page, [{ name: 'picture.png', bytes: [1, 2, 3] }]);
  await expect(page.locator('#lite-toasts')).toContainText('picture.png');
  expect(await paths(page)).toEqual(before);
});

test('the project is exported as a .zip that was made in the browser: a local file, not a link', async ({ page }) => {
  await fresh(page);
  await useDemo(page, 'basic-drums');
  const download = page.waitForEvent('download');
  await page.click('#lite-export');
  const file = await download;
  expect(file.suggestedFilename()).toBe('basic-drums.zip');
  expect(file.url()).toMatch(/^blob:/);   // an address that only this browser tab has: it cannot be given to somebody else
  await expect(page.locator('#lite-toasts')).toContainText('not a link');
  const size = (await (await import('node:fs')).promises.stat((await file.path())!)).size;
  expect(size).toBeGreaterThan(1000);
});
