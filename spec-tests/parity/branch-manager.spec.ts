import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('the branch manager shows the tree with search and fold controls', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-branches');
  await expect(page.locator('#bm')).toBeVisible();
  await expect(page.locator('#bm-search')).toBeVisible();
  await expect(page.locator('#bmscroll')).toBeVisible();
  await page.click('#bm-foldall');
  await page.click('#bm-unfoldall');
  await page.click('#vs-blocks');
  await expect(page.locator('#bm')).toBeHidden();
});

test('right-clicking a branch opens actions for its tip', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-branches');
  await page.locator('.bmbox[data-line="studio"]').click({ button: 'right' });
  await expect(page.locator('#menu')).toBeVisible();
  await expect(page.locator('#menu')).toContainText('Go to tip');
  await expect(page.locator('#menu')).toContainText('Look only');
  await expect(page.locator('#menu')).toContainText('New branch from tip');
  await expect(page.locator('#menu')).toContainText('Tag tip');
  await expect(page.locator('#menu')).not.toContainText('Drop...');
  await page.locator('.bmbox[data-line="garage"]').click({ button: 'right' });
  await expect(page.locator('#menu')).toContainText('Drop...');
});
