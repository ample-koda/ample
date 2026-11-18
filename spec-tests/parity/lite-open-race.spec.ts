import { expect, Page, test } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// Remix: the opening of a file is owned by the newest request. A slow answer of an older open must not put its file in the editor, take the labels, replace the
// language client or the editor, or send the next change to the wrong file.
freshServer({ env: { AMPLE_LITE_BUILDS_PER_MINUTE: '500', AMPLE_LITE_BUILDS_PER_HOUR: '5000' } });

const liteUrl = () => launchUrl().replace('/#token=', '/remix/?hooks=1#token=');

async function fresh(page: Page) {
  await page.goto(liteUrl());
  await page.waitForSelector('html[data-lite-ready="true"]');
  await page.evaluate(() => new Promise<void>((resolve) => { const request = indexedDB.deleteDatabase('ample-remix'); request.onsuccess = request.onerror = request.onblocked = () => resolve(); }));
  await page.reload();
  await page.waitForSelector('html[data-lite-ready="true"]');
}

test('a slow answer of an older open does not take the editor from the file that was opened after it', async ({ page }) => {
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
  await page.click('#lite-new-file');
  await page.fill('#lite-dialog-field', 'second.amplet');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-filename')).toHaveText('second.amplet');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');

  // the answer of the store for song.amplet is held
  await page.evaluate(() => {
    const w = window as any, store = w.__remixTest.app.store, real = store.readFile.bind(store);
    w.__restore = () => { store.readFile = real; };
    w.__release = () => {};
    store.readFile = (id: string, path: string) => (path === 'song.amplet' ? new Promise((resolve) => { w.__release = () => resolve(real(id, path)); }) : real(id, path));
  });
  await page.locator('#lite-tree .lite-row[data-path="song.amplet"]').click();   // the older open: it waits for the store
  await page.waitForTimeout(300);
  await page.locator('#lite-tree .lite-row[data-path="second.amplet"]').click();   // the newer open finishes at once
  await expect(page.locator('#lite-filename')).toHaveText('second.amplet');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');
  const before = await page.evaluate(() => {
    const app = (window as any).__remixTest.app;
    (window as any).__language = app.language;
    (window as any).__editor = app.editor;
    return { active: app.active };
  });
  expect(before.active).toBe('second.amplet');

  await page.evaluate(() => (window as any).__release());   // now the old answer arrives
  await page.evaluate(() => (window as any).__restore());
  await page.waitForTimeout(800);
  const after = await page.evaluate(() => {
    const w = window as any, app = w.__remixTest.app;
    return {
      active: app.active, label: document.querySelector('#lite-filename')!.textContent, sameLanguage: app.language === w.__language,
      sameEditor: app.editor === w.__editor, editors: document.querySelectorAll('#lite-editor .cm-editor').length,
    };
  });
  expect(after).toEqual({ active: 'second.amplet', label: 'second.amplet', sameLanguage: true, sameEditor: true, editors: 1 });
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');
  await expect(page.locator('#lite-editor .cm-content')).not.toContainText('PIXEL QUEST');

  // the next change goes to the file that is shown
  await page.locator('#lite-editor .cm-content').click();
  await page.keyboard.press('Control+End');
  await page.keyboard.type('\n# typed after the old answer');
  await page.waitForTimeout(900);   // saved
  const saved = await page.evaluate(async () => {
    const app = (window as any).__remixTest.app;
    return { second: (await app.store.readFile(app.project.id, 'second.amplet')).text, song: (await app.store.readFile(app.project.id, 'song.amplet')).text };
  });
  expect(saved.second).toContain('# typed after the old answer');
  expect(saved.song).not.toContain('# typed after the old answer');
  expect(saved.song).toContain('PIXEL QUEST');
});

test('another project ends an open that is on its way', async ({ page }) => {
  await fresh(page);
  await page.click('#lite-demos-btn');
  await page.locator('#lite-demo-list li[data-demo="pixel-quest"] button').click();
  await expect(page.locator('#lite-editor .cm-content')).toContainText('PIXEL QUEST');
  await page.evaluate(() => {
    const w = window as any, store = w.__remixTest.app.store, real = store.readFile.bind(store), first = w.__remixTest.app.project.id;   // only the project that is left is slow
    w.__restore = () => { store.readFile = real; };
    store.readFile = (id: string, path: string) => (id === first && path === 'song.amplet' ? new Promise((resolve) => { w.__release = () => resolve(real(id, path)); }) : real(id, path));
  });
  await page.locator('#lite-tree .lite-row[data-path="song.amplet"]').click();   // an open on its way
  await page.waitForTimeout(300);
  await page.click('#lite-new-project');
  await page.fill('#lite-dialog-field', 'Other');
  await page.click('#lite-dialog-ok');
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');
  await page.evaluate(() => (window as any).__release());
  await page.evaluate(() => (window as any).__restore());
  await page.waitForTimeout(800);
  await expect(page.locator('#lite-editor .cm-content')).toContainText('A starter song');
  await expect(page.locator('#lite-editor .cm-content')).not.toContainText('PIXEL QUEST');
  expect(await page.evaluate(() => document.querySelectorAll('#lite-editor .cm-editor').length)).toBe(1);
});
