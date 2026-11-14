import { test, expect, type Page } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// A failed Save on the General tab: the message stays in the tab (role alert), a message about a field marks and focuses that field, a message that is not about a field marks none,
// the button works again, and repeated failures do not pile ids up in aria-describedby.
freshServerPerTest();

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#btn-projects'); await page.click('#pm [data-page="settings"]'); });

const marked = (page: Page) => page.locator('#ps-tab-general [aria-invalid="true"]');

test('a refused value marks and focuses its field, keeps the message in the tab and lets the person save again', async ({ page }) => {
  const limit = (await page.evaluate(async () => (await (await fetch('/api/project/settings', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' } })).json()).parallelismLimit)) as number;
  for (let round = 1; round <= 2; round++) {
    await page.locator('#ps-parallelism').fill(String(limit + 1));
    await page.click('#ps-save');
    await expect(page.locator('#ps-tab-general .ps-error[role="alert"]')).toContainText('AMP5941');
    await expect(page.locator('#ps-parallelism')).toHaveAttribute('aria-invalid', 'true');
    await expect(page.locator('#ps-parallelism')).toBeFocused();
    await expect(marked(page)).toHaveCount(1);
    await expect(page.locator('#ps-save')).toBeEnabled();
    // the ids the field points to: its help and the message, each once, however often the save failed
    const ids = ((await page.locator('#ps-parallelism').getAttribute('aria-describedby')) || '').split(/\s+/).filter(Boolean);
    expect(ids, `round ${round}`).toEqual(['ps-parallelism-help', 'ps-error-general']);
  }
  await page.locator('#ps-parallelism').fill('1');
  await page.click('#ps-save');
  await expect(page.locator('#ps-tab-general .ps-error')).toHaveCount(0);
  await expect(page.locator('#ps-tab-general [aria-invalid]')).toHaveCount(0);
});

test('a failure that is not about a field marks no field, but still says so in the tab', async ({ page }) => {
  await page.route((u) => u.pathname === '/api/project/settings', (route) => (route.request().method() === 'POST' ? route.abort() : route.continue()));
  await page.locator('#ps-budget').fill('64');
  await page.click('#ps-save');
  await expect(page.locator('#ps-tab-general .ps-error[role="alert"]')).toBeVisible();
  await expect(marked(page)).toHaveCount(0);   // no unrelated field is called invalid
  await expect(page.locator('#ps-save')).toBeEnabled();
});

test('an ordinary word in a server message does not pick a field: "The disk is full." is not about "full detail (days)"', async ({ page }) => {
  await page.route((u) => u.pathname === '/api/project/settings', (route) => (route.request().method() === 'POST'
    ? route.fulfill({ status: 500, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Server error', detail: 'The disk is full.', code: 'AMP5001' }) })
    : route.continue()));
  await page.locator('#ps-budget').fill('64');
  await page.click('#ps-save');
  await expect(page.locator('#ps-tab-general .ps-error[role="alert"]')).toContainText('The disk is full.');
  await expect(marked(page)).toHaveCount(0);
});
