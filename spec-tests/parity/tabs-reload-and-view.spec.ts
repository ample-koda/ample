import { test, expect, type Page } from '@playwright/test';
import { api, freshServerPerTest, openApp } from '../../web/tests/support';

// Two edges of the tab row: a reload is not leaving (the blank new song stays), and the view of a window that was put back is applied even when the open takes long.
freshServerPerTest();

const songPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);
const docs = async () => (await api('/api/docs')) as string[];

/** song.amplet closed, so the blank Untitled song is the open one. */
async function blankUntitledOpen(page: Page) {
  await openApp(page, { reset: false });
  await page.locator('#tabbar .tab[data-path="song.amplet"] .tab-x').click();
  await expect(page.locator('#tabbar .tab.on[data-path="Untitled.amplet"]')).toBeVisible({ timeout: 10000 });
  await expect.poll(() => songPath(page)).toBe('Untitled.amplet');
  await page.waitForTimeout(900);   // the blank-song cleanup of the tab row has run
  expect(await docs()).toContain('Untitled.amplet');
}

test('reloading the page with a blank new song leaves a consistent editor: the blank file is gone and the song that was open before is shown, every time', async ({ page }) => {
  await blankUntitledOpen(page);
  for (let round = 1; round <= 4; round++) {
    await page.reload();
    await page.waitForSelector('body[data-ready="1"]');
    await expect(page.locator('#tabbar .tab.on')).toHaveCount(1, { timeout: 10000 });
    const shown = await songPath(page);
    await page.waitForTimeout(700);
    const files = await docs();
    expect(files, `round ${round}: the song the editor shows must exist (it shows ${shown})`).toContain(String(shown));
    expect(files).not.toContain('Untitled.amplet');
    await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', String(shown));
  }
});

test('leaving the page (not a reload) removes the blank new song file', async ({ page }) => {
  await blankUntitledOpen(page);
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: false })));
  await expect.poll(async () => (await docs()).includes('Untitled.amplet'), { timeout: 10000 }).toBe(false);
});

test('the view of a window that was put back is applied also when the file opens slowly', async ({ page, context }) => {
  await openApp(page, { reset: false });
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token'));
  const url = new URL(page.url());
  await api('/api/file', { method: 'PUT', body: { path: 'again.txt', text: 'one\ntwo\nthree\n' } });
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open-file', { detail: 'again.txt' })));
  const detached = await context.newPage();
  await detached.goto(`${url.origin}${url.pathname}?tab=again.txt#token=${token}`);
  await expect(page.locator('#tabbar .tab[data-path="again.txt"].remote')).toBeVisible({ timeout: 10000 });
  await expect.poll(() => detached.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).value).catch(() => '')).toBe('one\ntwo\nthree\n');
  await detached.locator('#se-ta').evaluate((e) => { const ta = e as HTMLTextAreaElement; ta.focus(); ta.setSelectionRange(8, 8); });   // the caret at the start of line 3
  // the main window reads the file slowly from now on: longer than the timers that used to carry the view (700 and 1800 ms)
  await page.route((u) => u.pathname === '/api/file' && u.searchParams.get('path') === 'again.txt', async (route) => { await new Promise((r) => setTimeout(r, 2600)); await route.continue(); });
  await page.locator('#tabbar .tab[data-path="again.txt"]').click();
  await page.getByRole('button', { name: 'Take it back here' }).click();
  await expect(page.locator('#tabbar .tab.on[data-path="again.txt"]')).toBeVisible({ timeout: 15000 });
  await expect.poll(() => page.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).selectionStart).catch(() => -1), { timeout: 15000 }).toBe(8);
});
