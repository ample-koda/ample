import { test, expect } from '@playwright/test';
import { api, freshServerPerTest, openApp } from '../../web/tests/support';

freshServerPerTest();

test('project file picker preselects a file and offers an explicit empty choice', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { (window as Window & { pickerResult: Promise<string | undefined> }).pickerResult = import('/app/filepicker.js').then((module) => module.openProjectFilePicker('song.amplet', [], 'file')); });
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  await expect(picker.locator('[role=option].sel')).toContainText('song.amplet');
  await picker.getByRole('button', { name: 'Use no file' }).click();
  expect(await page.evaluate(() => (window as Window & { pickerResult: Promise<string | undefined> }).pickerResult)).toBe('');
});

test('file parameter picker and clear control commit their selected value', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => {
    const target = window as Window & { fieldSaved: string };
    target.fieldSaved = '';
    import('/app/fields.js').then(({ fieldFor }) => document.body.append(fieldFor('file', { kind: 'file', accept: ['.amplet'] }, 'song.amplet', { disabled: false, title: '', save: (value) => { target.fieldSaved = value; } })));
  });
  const field = page.locator('.file-field').last();
  await field.getByRole('button', { name: 'Choose file' }).click();
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  await picker.getByRole('button', { name: 'Choose' }).click();
  await expect.poll(() => page.evaluate(() => (window as Window & { fieldSaved: string }).fieldSaved)).toBe('song.amplet');
  await field.getByRole('button', { name: 'Clear file' }).click();
  await expect.poll(() => page.evaluate(() => (window as Window & { fieldSaved: string }).fieldSaved)).toBe('');
});

test('the file arrow streams linked project audio through the shared player', async ({ page }) => {
  await openApp(page, { reset: false });
  const wav = Buffer.alloc(60);
  wav.write('RIFF', 0); wav.writeUInt32LE(52, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24); wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(16, 40);
  await page.evaluate(async (encoded) => {
    const raw = atob(encoded), bytes = Uint8Array.from(raw, (char) => char.charCodeAt(0));
    const file = new File([bytes], 'preview.wav', { type: 'audio/wav' });
    await (window as Window & { AMPLE_API: { uploadProject: Function } }).AMPLE_API.uploadProject(file, 'samples', 'refuse', () => {});
    const { fieldFor } = await import('/app/fields.js');
    document.body.append(fieldFor('file', { kind: 'file', accept: ['.wav'] }, 'samples/preview.wav', { disabled: false, title: '', save: () => {} }));
  }, wav.toString('base64'));
  const field = page.locator('.file-field').last();
  await field.getByRole('button', { name: 'Open file' }).click();
  await expect.poll(() => page.evaluate(() => (window as Window & { AMPLE_PLAYER: { state: () => { source: string | null } } }).AMPLE_PLAYER.state().source)).toBe('samples/preview.wav');
});

test('file fields flag missing paths and sampleset fields choose folders', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(async () => {
    const { fieldFor } = await import('/app/fields.js');
    await (window as Window & { AMPLE_API: { createProjectFolder: (path: string) => Promise<unknown> } }).AMPLE_API.createProjectFolder('picker-samples');
    document.body.append(fieldFor('file', { kind: 'file', accept: ['.wav'] }, 'missing.wav', { disabled: false, title: '', save: () => {} }));
    document.body.append(fieldFor('sampleset', { kind: 'sampleset' }, '', { disabled: false, title: '', save: (value) => { (window as Window & { samplesetSaved: string }).samplesetSaved = value; } }));
    document.body.append(fieldFor('file', { kind: 'file' }, 'readonly.wav', { disabled: true, title: '', save: () => {} }));
  });
  const fields = page.locator('body > .file-field');
  await expect(fields.first().locator('input')).toHaveAttribute('aria-invalid', 'true');
  await expect(fields.last().getByRole('button', { name: 'Choose file' })).toBeDisabled();
  await fields.nth(1).getByRole('button', { name: 'Choose file' }).click();
  const picker = page.locator('.project-file-picker');
  await expect(picker.getByRole('heading', { name: 'Choose a project folder' })).toBeVisible();
  await picker.getByRole('option', { name: 'picker-samples' }).click();
  await picker.getByRole('button', { name: 'Choose' }).click();
  await expect.poll(() => page.evaluate(() => (window as Window & { samplesetSaved: string }).samplesetSaved)).toBe('picker-samples');
});

test('project uploads expose per-file progress and the file list windows large folders', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { (window as Window & { pickerResult: Promise<string | undefined> }).pickerResult = import('/app/filepicker.js').then((module) => module.openProjectFilePicker('', ['.wav'], 'file')); });
  const picker = page.locator('.project-file-picker');
  await picker.getByLabel('Upload files').setInputFiles({ name: 'progress.wav', mimeType: 'audio/wav', buffer: Buffer.alloc(2048, 7) });
  await expect(picker.locator('.project-upload-row')).toContainText('progress.wav');
  await expect(picker.locator('.project-upload-row')).toContainText('Complete');
  await page.evaluate(async () => {
    const { renderProjectFileList } = await import('/app/filelist.js');
    const host = document.createElement('div'); host.style.cssText = 'height:240px;overflow:auto'; document.body.append(host);
    const items = Array.from({ length: 2101 }, (_, index) => ({ name: `sample-${index}.wav`, path: `sample-${index}.wav`, kind: 'audio', isDir: false }));
    (window as Window & { virtualHost: HTMLDivElement }).virtualHost = host;
    renderProjectFileList(host, items, '', ['.wav'], '', () => {});
  });
  const list = page.locator('body > div[role=listbox]').last();
  expect(await list.locator('[role=option]').count()).toBeLessThan(40);
  await list.evaluate((element) => { element.scrollTop = 104 * 1200; element.dispatchEvent(new Event('scroll')); });
  await expect(list.locator('[data-path="sample-1200.wav"]')).toBeAttached();
  await picker.getByRole('button', { name: 'Cancel' }).click();
});

test('picker creates a folder and offers all upload conflict choices', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { (window as Window & { pickerResult: Promise<string | undefined> }).pickerResult = import('/app/filepicker.js').then((module) => module.openProjectFilePicker('', ['.wav'], 'file')); });
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  await picker.getByRole('button', { name: 'New folder' }).click();
  await page.locator('#dlg-extra input[aria-label="Folder name"]').fill('picker-folder');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Create' }).click();
  await page.waitForTimeout(800);   // the new folder is open (tree and list) before the file goes in
  await picker.getByLabel('Upload files').setInputFiles({ name: 'picker.wav', mimeType: 'audio/wav', buffer: Buffer.from([1, 2, 3]) });
  await expect(picker.locator('[role=option]', { hasText: 'picker.wav' })).toHaveCount(1);
  await picker.getByLabel('Upload files').setInputFiles({ name: 'picker.wav', mimeType: 'audio/wav', buffer: Buffer.from([4, 5, 6]) });
  const conflictButtons = page.locator('#dlg-btns');
  await expect(conflictButtons.getByRole('button', { name: 'Refuse' })).toBeVisible();
  await expect(conflictButtons.getByRole('button', { name: 'Overwrite', exact: true })).toBeVisible();
  await expect(conflictButtons.getByRole('button', { name: 'Keep both' })).toBeVisible();
  await expect(conflictButtons.getByRole('button', { name: 'Overwrite and record' })).toBeVisible();
  await page.locator('#dlg-btns').getByRole('button', { name: 'Keep both' }).click();
  // a new folder opens at once, so the uploads and the kept copy are in it
  await expect(picker.locator('[role=option]')).toHaveCount(2);
  await picker.getByRole('button', { name: 'Cancel' }).click();
});

test('right click in the picker list offers Add > Folder, on an empty spot and on a folder', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { (window as Window & { pickerResult: Promise<string | undefined> }).pickerResult = import('/app/filepicker.js').then((module) => module.openProjectFilePicker('', ['.wav'], 'file')); });
  const picker = page.locator('.project-file-picker');
  await expect(picker).toBeVisible();
  const list = picker.locator('.project-file-list');
  const box = (await list.boundingBox())!;
  await page.mouse.click(box.x + box.width - 6, box.y + box.height - 6, { button: 'right' });
  await page.locator('#menu div', { hasText: /^Add/ }).click();
  await page.locator('#menu .flyout > div', { hasText: /^Folder$/ }).click();
  await page.locator('#dlg-extra input[aria-label="Folder name"]').fill('menu-made');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Create' }).click();
  // the new folder is open now: the next menu on an empty spot adds a folder inside it
  await page.mouse.click(box.x + box.width - 6, box.y + box.height - 6, { button: 'right' });
  await page.locator('#menu div', { hasText: /^Add/ }).click();
  await page.locator('#menu .flyout > div', { hasText: /^Folder$/ }).click();
  await page.locator('#dlg-extra input[aria-label="Folder name"]').fill('inside');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Create' }).click();
  await expect.poll(async () => JSON.stringify(await api('/api/project/tree?path=menu-made&depth=0'))).toContain('menu-made/inside');
  await picker.getByRole('button', { name: 'Cancel' }).click();
});

test('the picker keeps one fixed layout: compact rows, details and buttons inside the dialog, nothing overlapping', async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => { const m = await import('/app/filepicker.js'); (window as any).__picked = m.openProjectFilePicker('', [], 'file'); });
  const dialog = page.locator('dialog.project-file-picker');
  await expect(dialog).toBeVisible();
  const box = (await dialog.boundingBox())!;
  const rows = dialog.locator('.project-file-list .file-list-row');
  await expect.poll(() => rows.count()).toBeGreaterThan(0);
  expect((await rows.first().boundingBox())!.height).toBeLessThan(44);
  for (const name of ['Choose', 'Cancel', 'Use no file', 'New folder', 'Upload...']) {
    const b = (await dialog.getByRole('button', { name, exact: true }).boundingBox())!;
    expect(b.y + b.height).toBeLessThanOrEqual(box.y + box.height);
    expect(b.x + b.width).toBeLessThanOrEqual(box.x + box.width);
  }
  const list = (await dialog.locator('.project-file-list').boundingBox())!, foot = (await dialog.locator('.project-picker-foot').boundingBox())!;
  expect(list.y + list.height).toBeLessThanOrEqual(foot.y + 1);
  await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
});
