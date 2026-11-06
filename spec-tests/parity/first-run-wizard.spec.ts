import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The first-time setup wizard (plan/first-run-wizard.md): the project settings as pages, walked through once, before the studio goes on.
freshServer({ firstRun: true });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const pending = (page: Page) => page.evaluate(async () => (await (await fetch('/api/project/first-run', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' } })).json()).pending);
const openPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);

test('the wizard shows first, nothing behind it can be used and Escape does not close it', async ({ page }) => {
  await expect(page.locator('#fr')).toBeVisible();
  await expect(page.locator('#fr-title')).toHaveText('Welcome');
  await expect(page.locator('#fr .fr-steps li')).toHaveCount(8);
  await page.keyboard.press('Escape');
  await expect(page.locator('#fr')).toBeVisible();
  expect(await page.evaluate(() => [...document.body.children].some((node) => (node as HTMLElement).inert))).toBe(true);
  await expect(page.locator('#fr-back')).toBeDisabled();
});

test('F5 before Finish starts again at the first step, with what was saved on the earlier steps filled in', async ({ page }) => {
  await page.locator('#fr-next').click();   // welcome -> general
  await expect(page.locator('#fr-title')).toHaveText('General');
  await page.locator('#ps-budget').fill('123');
  await page.locator('#fr-next').click();   // saves the page, goes on to audio routing
  await expect(page.locator('#fr-title')).toHaveText('Audio routing');
  await page.reload();
  await expect(page.locator('#fr-title')).toHaveText('Welcome');
  expect(await pending(page)).toBe(true);   // nothing finished it
  await page.locator('#fr-next').click();
  await expect(page.locator('#ps-budget')).toHaveValue('123');
});

test('walking every page and pressing Finish closes the wizard for good and opens the demo song of a new project', async ({ page }) => {
  for (const title of ['General', 'Audio routing', 'Web search', 'Safe saves', 'Assistant', 'Seed and folders', 'Ready']) {
    await page.locator('#fr-next').click();
    await expect(page.locator('#fr-title')).toHaveText(title);
  }
  await expect(page.locator('#fr-next')).toHaveText('Finish');
  await page.locator('#fr-next').click();
  await expect(page.locator('#fr')).toHaveCount(0);
  expect(await pending(page)).toBe(false);
  await expect.poll(() => openPath(page)).toBe('demo.amplet');
  await page.reload();
  await expect(page.locator('#fr')).toHaveCount(0);
});
