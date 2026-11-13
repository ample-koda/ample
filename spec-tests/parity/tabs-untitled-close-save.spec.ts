import { test, expect, type Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Close on an edited new song (Untitled) with Save: the naming question writes the song to its new place and the close goes on. The song under its new name is not opened
// (an open that lands after the close would bring its tab back), and the neighbouring tab is the active one afterwards. Cancel keeps Untitled open.
freshServer();

const songPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);
const untitled = (page: Page) => page.locator('#tabbar .tab[data-path="Untitled.amplet"]');

/** An edited Untitled next to song.amplet, Untitled active. */
async function editedUntitledWithNeighbour(page: Page) {
  await openApp(page, { reset: false });
  await page.locator('#tabbar .tab[data-path="song.amplet"] .tab-x').click();
  await expect(page.locator('#tabbar .tab.on[data-path="Untitled.amplet"]')).toBeVisible({ timeout: 10000 });
  await expect.poll(() => songPath(page)).toBe('Untitled.amplet');
  await page.evaluate(async () => { const s = await import('/app/store.js'); await s.applyText(`${s.S.doc.text}# a change\n`); });
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'song.amplet' })));   // the neighbour, as the Project panel opens it
  await expect(page.locator('#tabbar .tab.on[data-path="song.amplet"]')).toBeVisible({ timeout: 10000 });
  await expect(untitled(page)).toHaveClass(/dirty/, { timeout: 10000 });
  await untitled(page).click();
  await expect.poll(() => songPath(page)).toBe('Untitled.amplet');
  await expect(page.locator('#tabbar .tab.on[data-path="Untitled.amplet"]')).toBeVisible();
}

async function closeAndChooseSave(page: Page) {
  await untitled(page).locator('.tab-x').click();
  await expect(page.locator('#dlg-b')).toContainText('has unsaved changes');
  await page.locator('#dlg-btns button', { hasText: /^Save$/ }).click();
  await expect(page.locator('#untitled-name')).toBeVisible();
}

test('Close then Save on an edited Untitled saves it under the new name without opening it, and the neighbouring tab is active', async ({ page }) => {
  await editedUntitledWithNeighbour(page);
  const docRequests: string[] = [];
  page.on('request', (r) => { const u = new URL(r.url()); if (u.pathname === '/api/doc' && r.method() === 'GET') docRequests.push(u.searchParams.get('path') || ''); });
  await page.evaluate(() => { (window as any).__opens = []; document.addEventListener('ample:open', (e) => (window as any).__opens.push((e as CustomEvent).detail), true); });
  await closeAndChooseSave(page);
  await page.locator('#untitled-name').fill('closed-save');
  await page.locator('#dlg-btns button', { hasText: /^Save$/ }).click();
  await expect.poll(async () => (await api('/api/docs') as string[]).includes('closed-save.amplet')).toBe(true);
  await expect(page.locator('#tabbar .tab.on[data-path="song.amplet"]')).toBeVisible({ timeout: 10000 });
  await expect.poll(() => songPath(page)).toBe('song.amplet');
  await page.waitForTimeout(1500);   // an open of the new name that was on its way would have landed by now
  await expect(page.locator('#tabbar .tab[data-path="closed-save.amplet"]')).toHaveCount(0);
  await expect(untitled(page)).toHaveCount(0);
  await expect(page.locator('#tabbar .tab.on[data-path="song.amplet"]')).toBeVisible();
  expect(await page.evaluate(() => (window as any).__opens)).not.toContain('closed-save.amplet');
  expect(docRequests).not.toContain('closed-save.amplet');
  expect(await songPath(page)).toBe('song.amplet');
});

test('Close then Save, cancelled at the naming question, keeps Untitled open', async ({ page }) => {
  await editedUntitledWithNeighbour(page);
  await closeAndChooseSave(page);
  await page.locator('#dlg-btns button', { hasText: 'Cancel' }).click();
  await expect(page.locator('#untitled-name')).toBeHidden();
  await expect(page.locator('#tabbar .tab.on[data-path="Untitled.amplet"]')).toBeVisible();
  await expect(untitled(page)).toHaveClass(/dirty/);
  expect(await songPath(page)).toBe('Untitled.amplet');
});
