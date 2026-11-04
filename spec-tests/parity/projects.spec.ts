import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#btn-projects'); });

test('the recent list shows this project with its health summary', async ({ page }) => {
  const card = page.locator('.card[data-project]').first();
  await expect(card).toContainText('open here');
  await expect(card).toContainText('found');
  await expect(card).toContainText(/backup/);
});

test('a project that was created can be forgotten again without touching its folder', async ({ page }) => {
  const r = await api('/api/projects/new', { method: 'POST', body: { name: 'gone', folder: `${process.env.TEMP ?? '/tmp'}/ample-gone-${Date.now()}` } });
  expect(JSON.stringify(r)).toContain('gone');
  await page.click('#pm [data-page="recent"]');
  await expect(page.locator('.card[data-project="gone"]')).toBeVisible();
  await page.locator('.card[data-project="gone"] button', { hasText: 'Forget' }).click();
  await expect(page.locator('.card[data-project="gone"]')).toHaveCount(0);
});

test('the pages can be left again', async ({ page }) => {
  await page.click('#pm [data-page="new"]');
  await expect(page.locator('#pm-new-create')).toBeVisible();
  await page.click('#pm-close');
  await expect(page.locator('#pm')).toBeHidden();
});
