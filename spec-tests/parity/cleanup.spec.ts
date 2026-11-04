import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('the cleanup shows the dry run and Cancel leaves the history alone', async ({ page }) => {
  await openApp(page, { reset: false });
  const before = JSON.stringify(await api('/api/history/graph'));
  await page.click('#btn-projects');
  await page.click('#pm [data-page="backup"]');
  await page.click('#cl-go');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#dlg-b')).toContainText(/revision|byte/i);
  await dialog(page, 'Cancel');
  expect(JSON.stringify(await api('/api/history/graph'))).toBe(before);
});
