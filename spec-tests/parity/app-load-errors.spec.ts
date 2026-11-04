import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// The page loads and shows its tabs without a script error (a missing import once went unnoticed because the rest of the page kept working).
test('the app loads without script errors, on a wide screen and on a narrow one', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await openApp(page);
  for (const tab of ['struct', 'tpl', 'lib']) await page.locator(`#ltabs button[data-l="${tab}"]`).click();
  await page.setViewportSize({ width: 390, height: 800 });
  await page.waitForTimeout(500);
  await page.locator('#vs-source').click();
  await page.locator('#vs-blocks').click();
  await page.waitForTimeout(500);
  expect(errors).toEqual([]);
});
