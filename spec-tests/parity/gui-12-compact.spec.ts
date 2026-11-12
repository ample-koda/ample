import { test, expect } from '@playwright/test';
import { freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 12: on a desktop (a fine pointer) the controls of the library and the Parameters panel are compact (26 px fields, tight rows).
freshServer();
test('fields and library rows are compact on a desktop', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  const search = await page.locator('#lib-q').evaluate((el) => el.getBoundingClientRect().height);
  expect(search).toBeLessThanOrEqual(28);
  const row = await page.locator('#lib .tplitem').first().evaluate((el) => el.getBoundingClientRect().height);
  expect(row).toBeLessThanOrEqual(30);
  await node(page, 'drums').locator('.nh .t').click();
  const field = await page.locator('#right input[type="text"], #right input:not([type])').first().evaluate((el) => el.getBoundingClientRect().height);
  expect(field).toBeLessThanOrEqual(28);
});
