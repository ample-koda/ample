import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The first-time setup wizard when the project settings cannot be read: no way forward until they are there, Retry tries again, Later opens the project as it is and the setup stays pending.
freshServer({ firstRun: true });   // the setup stays pending in both tests, so each one meets the wizard

const pending = (page: Page) => page.evaluate(async () => (await (await fetch('/api/project/first-run', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' } })).json()).pending);

async function openWithFailingSettings(page: Page) {
  const state = { failing: true };
  await page.route((u) => u.pathname === '/api/project/settings', (route) => (state.failing && route.request().method() === 'GET' ? route.abort() : route.continue()));
  await openApp(page, { reset: false });
  await expect(page.locator('#fr-retry')).toBeVisible({ timeout: 15000 });
  return state;
}

test('while the settings cannot be read Next and Back stay off, and Retry brings the pages once they can', async ({ page }) => {
  const state = await openWithFailingSettings(page);
  await expect(page.locator('#fr-next')).toBeDisabled();
  await expect(page.locator('#fr-back')).toBeDisabled();
  await page.locator('#fr-next').click({ force: true, timeout: 2000 }).catch(() => {});   // pressing it does nothing
  await expect(page.locator('#fr-retry')).toBeVisible();
  state.failing = false;
  await page.locator('#fr-retry').click();
  await expect(page.locator('#fr-retry')).toHaveCount(0);
  await expect(page.locator('#fr-next')).toBeEnabled();
  await page.locator('#fr-next').click();
  await expect(page.locator('#fr-title')).toHaveText('General');
  await expect(page.locator('#ps-budget')).toBeVisible();
});

test('Later opens the project as it is and the setup stays pending', async ({ page }) => {
  await openWithFailingSettings(page);
  await page.locator('#fr-later').click();
  await expect(page.locator('#fr')).toHaveCount(0);
  expect(await page.evaluate(() => [...document.body.children].some((node) => (node as HTMLElement).inert))).toBe(false);
  expect(await pending(page)).toBe(true);
});
