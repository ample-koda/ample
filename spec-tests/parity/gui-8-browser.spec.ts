import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// GUI flow 8: the block library has a star on every item (favourites come first), a Recent section with the blocks that were added last, and / or F8 focus its search box.
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { try { localStorage.setItem('ample.lib.fav', '[]'); localStorage.setItem('ample.lib.recent', '[]'); } catch { /* none */ } });
  await page.reload();
});

test('a star puts a block under Favourites and a second click takes it away', async ({ page }) => {
  const item = page.locator('#lib .tplitem[data-tpl="drums"]').first();
  await item.hover();
  await item.locator('.tplstar').click();
  await expect(page.locator('#lib-fav .tplitem[data-tpl="drums"]')).toHaveCount(1);
  await page.locator('#lib .tplitem[data-tpl="drums"] .tplstar.on').first().click();
  await expect(page.locator('#lib-fav')).toHaveCount(0);
});

test('a block that is added shows up under Recent', async ({ page }) => {
  await page.locator('#lib .tplitem[data-tpl="synth"]').first().dblclick();
  await page.reload();
  await expect(page.locator('#lib-recent .tplitem[data-tpl="synth"]')).toHaveCount(1);
});

test('/ and F8 put the cursor in the search box', async ({ page }) => {
  await page.locator('#cv').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('/');
  await expect(page.locator('#lib-q')).toBeFocused();
  await page.locator('#cv').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('F8');
  await expect(page.locator('#lib-q')).toBeFocused();
});
