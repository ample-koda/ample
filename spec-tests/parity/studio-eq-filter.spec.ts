import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('EQ and filter studio panels show editable response controls', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'eq', 'eq');
  const eq = page.locator('.node[data-id="eq"]');
  await eq.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog.locator('.studio-eq-panel .studio-response-graph')).toBeVisible();
  await expect(dialog.getByRole('slider', { name: 'Low band gain' })).toBeVisible();
  await page.keyboard.press('Escape');

  await addTemplateBlock(page, 'filter', 'filter');
  const filter = page.locator('.node[data-id="filter"]');
  await filter.getByRole('button', { name: 'Advanced', exact: true }).click();
  await expect(dialog.locator('.studio-filter-panel .studio-response-graph')).toBeVisible();
  await expect(dialog.getByRole('slider', { name: 'Cutoff' })).toBeVisible();
});
