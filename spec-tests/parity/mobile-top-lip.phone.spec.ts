import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// The top bar can be hidden with a lip under it (phone), and the full screen switch sits in the more menu. Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

test('the lip hides and shows the top bar and the choice is remembered', async ({ page }) => {
  const header = page.locator('header');
  const lip = page.locator('#lip-top');
  await expect(header).toBeVisible();
  await expect(lip).toBeVisible();
  const box = await lip.boundingBox();
  const bar = await header.boundingBox();
  expect(Math.abs(box!.y - (bar!.y + bar!.height))).toBeLessThan(3);   // right under the bar
  await lip.click();
  await expect(header).toBeHidden();
  await expect(lip).toHaveAttribute('aria-expanded', 'false');
  expect((await lip.boundingBox())!.y).toBeLessThan(2);                  // at the top edge
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(await page.evaluate(() => innerWidth));
  await page.reload();
  await expect(header).toBeHidden();
  await lip.click();
  await expect(header).toBeVisible();
});

test('the more menu has "Hide top bar" and, where the browser allows it, "Full screen"', async ({ page }) => {
  await page.locator('#btn-more').click();
  await expect(page.getByRole('menuitem', { name: 'Hide top bar' })).toBeVisible();
  const allowed = await page.evaluate(() => document.fullscreenEnabled);
  await expect(page.getByRole('menuitem', { name: 'Full screen' })).toHaveCount(allowed ? 1 : 0);
  await page.getByRole('menuitem', { name: 'Hide top bar' }).click();
  await expect(page.locator('header')).toBeHidden();
});

test('the page is an installable web app: manifest with a standalone display and icons', async ({ page }) => {
  const href = await page.locator('link[rel=manifest]').getAttribute('href');
  const manifest = await page.evaluate(async (url) => (await fetch(url!, { credentials: 'include' })).json(), href);
  expect(manifest.display).toBe('standalone');
  expect(manifest.icons.length).toBeGreaterThan(1);
  expect(await page.locator('meta[name=theme-color]').getAttribute('content')).toBeTruthy();
});

test('View > Block library and Properties open a drawer on a small screen, and Escape closes it', async ({ page }) => {
  for (const [name, side] of [['Block library', '#left'], ['Properties', '#right']] as const) {
    await page.locator('#btn-more').click();
    await page.getByRole('menuitem', { name }).click();
    await expect(page.locator(side)).toBeVisible();
    const box = await page.locator(side).boundingBox();
    expect(box!.width).toBeGreaterThan(200);
    await page.keyboard.press('Escape');
    await expect(page.locator(side)).toBeHidden();
  }
});
