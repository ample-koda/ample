import { test, expect } from '@playwright/test';
import { api, bottomTab, dialog, freshServer, openApp } from '../../web/tests/support';

freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

test('a forked line shows in the structure list and as a box next to its parent in the branch view', async ({ page }) => {
  const name = `leaf-${Date.now()}`;
  await bottomTab(page, 'hist');
  await page.click('#btn-fork');
  await dialog(page, 'Fork', [name]);
  await expect(page.locator('#b-line')).toContainText(name);
  await page.click('#ltabs button[data-l="struct"]');
  await expect(page.locator(`#pane-struct .rev[data-line="${name}"]`)).toBeVisible();
  await bottomTab(page, 'branch');
  await expect(page.locator('.brbox.this')).toHaveAttribute('data-line', name);
  expect(await page.locator('.brbox').count()).toBeGreaterThanOrEqual(2);
  expect(await page.locator('.brarrow').count()).toBeGreaterThanOrEqual(1);
});

test('clicking a branch box focuses it and double-click goes to its tip', async ({ page }) => {
  await bottomTab(page, 'branch');
  const other = page.locator('.brbox:not(.this)').first();
  const line = await other.getAttribute('data-line');
  await other.click();
  await expect(page.locator('.brbox.this')).toHaveAttribute('data-line', line!);
  await page.locator('.brbox.this').dblclick();
  await expect.poll(async () => (await api('/api/history/graph')).current).toBe(line);
});
