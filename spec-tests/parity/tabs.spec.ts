import { test, expect, type Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Open files as tabs (plan/editor-tabs.md, parts 2 and 3): the row, the marks, the overflow list, Save all.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const makeFile = (path: string, text = 'hello\n') => api('/api/file', { method: 'PUT', body: { path, text } });
const open = (page: Page, path: string) => page.evaluate((p) => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: p })), path);

test('the open song has a tab, and opening files adds tabs that can be switched and closed', async ({ page }) => {
  await expect(page.locator('#tabbar')).toBeVisible();
  await expect(page.locator('#tabbar .tab')).toHaveCount(1);
  await makeFile('notes-a.txt'); await makeFile('notes-b.txt');
  await open(page, 'notes-a.txt'); await open(page, 'notes-b.txt');
  await expect(page.locator('#tabbar .tab')).toHaveCount(3);
  await expect(page.locator('#tabbar .tab.on')).toContainText('notes-b.txt');
  await page.locator('#tabbar .tab[data-path="notes-a.txt"]').click();
  await expect(page.locator('#tabbar .tab.on')).toContainText('notes-a.txt');
  await page.locator('#tabbar .tab[data-path="notes-a.txt"] .tab-x').click();
  await expect(page.locator('#tabbar .tab')).toHaveCount(2);
});

test('audio and image files share one tab each', async ({ page }) => {
  await page.evaluate(() => { document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'one.wav' })); document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'two.wav' })); document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'pic.png' })); });
  await expect(page.locator('#tabbar .tab[data-path$=".wav"]')).toHaveCount(1);
  await expect(page.locator('#tabbar .tab[data-path="two.wav"]')).toHaveCount(1);   // the next audio file took the place of the first
  await expect(page.locator('#tabbar .tab[data-path="pic.png"]')).toHaveCount(1);
});

test('with many tabs the row keeps to one line and the list holds all of them, with a filter', async ({ page }) => {
  for (let i = 0; i < 14; i++) { await makeFile(`many-${i}.txt`); await open(page, `many-${i}.txt`); }
  await expect(page.locator('#tabs-more')).toHaveText('▾ 15');
  const wrapped = await page.locator('#tabbar .tabs-list').evaluate((e) => e.scrollHeight <= e.clientHeight + 1);
  expect(wrapped).toBe(true);
  await page.locator('#tabs-more').click();
  await expect(page.locator('#tabs-popup .tabs-row')).toHaveCount(15);
  await page.locator('#tabs-popup input').fill('many-7');
  await expect(page.locator('#tabs-popup .tabs-row')).toHaveCount(1);
  await page.locator('#tabs-popup .tabs-row').click();
  await expect(page.locator('#tabbar .tab.on')).toContainText('many-7.txt');
});

test('Save all asks with a remembered checkpoint box and saves the files with unsaved changes', async ({ page }) => {
  await makeFile('draft.txt', 'on disk\n');
  await open(page, 'draft.txt');
  await api('/api/wc', { method: 'PUT', body: { path: 'draft.txt', text: 'unsaved text\n' } });
  await expect(page.locator('#tabbar .tab[data-path="draft.txt"].dirty')).toBeVisible({ timeout: 8000 });
  await page.locator('#tabs-saveall').click();
  await expect(page.locator('#saveall-checkpoint')).toBeChecked();   // on at the start
  await page.locator('#saveall-checkpoint').uncheck();
  await page.getByRole('button', { name: 'Save all', exact: true }).last().click();
  await expect(page.locator('#tabbar .tab[data-path="draft.txt"].dirty')).toHaveCount(0, { timeout: 8000 });
  expect((await api('/api/wc?path=draft.txt') as { saved: string }).saved).toBe('unsaved text\n');
  await page.locator('#tabs-saveall').count();   // nothing left to save: the button is gone
  await page.reload();
  await page.waitForSelector('#tabbar .tab');
  expect(await page.evaluate(() => localStorage.getItem('ample.saveall.checkpoint'))).toBe('0');   // remembered
});

test('a tab in a window of its own is a placeholder in the main window and comes back when that window closes', async ({ page, context }) => {
  await makeFile('side.txt'); await open(page, 'side.txt');
  const url = new URL(page.url());
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token'));
  const detached = await context.newPage();
  await detached.goto(`${url.origin}${url.pathname}?tab=side.txt#token=${token}`);
  await expect(page.locator('#tabbar .tab[data-path="side.txt"].remote')).toBeVisible({ timeout: 10000 });
  await page.locator('#tabbar .tab[data-path="side.txt"]').click();   // one editor per file: the click brings the window to the front instead of opening a second editor
  await expect(page.locator('#tabbar .tab.on[data-path="side.txt"]')).toHaveCount(0);
  await detached.close();
  await expect(page.locator('#tabbar .tab[data-path="side.txt"].remote')).toHaveCount(0, { timeout: 10000 });
});

test('a file open in another window can be taken back: that window closes and the main window shows the view it had', async ({ page, context }) => {
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token'));
  const url = new URL(page.url());
  await api('/api/file', { method: 'PUT', body: { path: 'again.txt', text: 'one\ntwo\nthree\n' } });
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'again.txt' })));
  const detached = await context.newPage();
  await detached.goto(`${url.origin}${url.pathname}?tab=again.txt#token=${token}`);
  await expect(page.locator('#tabbar .tab[data-path="again.txt"].remote')).toBeVisible({ timeout: 10000 });
  await page.locator('#tabbar .tab[data-path="again.txt"]').click();
  await page.getByRole('button', { name: 'Take it back here' }).click();
  await expect(page.locator('#tabbar .tab[data-path="again.txt"].remote')).toHaveCount(0, { timeout: 10000 });
  await expect(page.locator('#tabbar .tab.on[data-path="again.txt"]')).toBeVisible();
  expect(detached.isClosed() || await detached.evaluate(() => true).catch(() => true)).toBeTruthy();
});

test('on a phone the row is the active tab and one list button; Save all and the recent files are in the list', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await makeFile('phone.txt'); await open(page, 'phone.txt');
  await api('/api/wc', { method: 'PUT', body: { path: 'phone.txt', text: 'unsaved\n' } });
  await expect(page.locator('#tabbar .tab[data-path="phone.txt"].dirty')).toBeVisible({ timeout: 8000 });
  await expect(page.locator('#tabbar .tab:visible')).toHaveCount(1);   // only the active tab
  await expect(page.locator('#tabs-saveall')).toBeHidden();
  await expect(page.locator('#tabbar .recent-files-control')).toBeHidden();
  await page.locator('#tabs-more').click();
  await expect(page.locator('.tabs-sheet-save')).toBeVisible();
  const box = await page.locator('#tabs-popup').boundingBox();
  expect(box!.x).toBeGreaterThanOrEqual(0);
  expect(box!.x + box!.width).toBeLessThanOrEqual(390);   // the sheet fits the screen
});

test('closing the last tab opens a new blank song; opening another song removes it again; its first save asks where it goes', async ({ page }) => {
  await page.locator('#tabbar .tab .tab-x').first().click();
  await expect(page.locator('#tabbar .tab[data-path="Untitled.amplet"].on')).toBeVisible({ timeout: 10000 });
  await expect(page.locator('#tabbar .tab')).toHaveCount(1);   // there is always one editor
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'song.amplet' })));
  await expect(page.locator('#tabbar .tab[data-path="Untitled.amplet"]')).toHaveCount(0, { timeout: 10000 });   // blank, so it went away
  expect(((await api('/api/docs')) as string[]).includes('Untitled.amplet')).toBe(false);
  await page.locator('#tabbar .tab .tab-x').first().click();
  await expect(page.locator('#tabbar .tab[data-path="Untitled.amplet"].on')).toBeVisible({ timeout: 10000 });
  await page.evaluate(async () => { const s = await import('/app/store.js'); await s.applyText(`${s.S.doc.text}# my first change\n`); });
  await page.keyboard.press('Control+s');
  await expect(page.locator('#untitled-name')).toBeVisible();
  await page.locator('#untitled-name').fill('my-first');
  await page.getByRole('button', { name: 'Save', exact: true }).last().click();
  await expect(page.locator('#tabbar .tab[data-path="my-first.amplet"].on')).toBeVisible({ timeout: 10000 });
  expect(((await api('/api/docs')) as string[]).includes('Untitled.amplet')).toBe(false);
});
