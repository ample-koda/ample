import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('compressor and duck studio panels expose their curve and pattern controls', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'comp');
  const compressor = page.locator('.node[data-id="comp"]');
  await compressor.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog.getByRole('img', { name: 'Compressor input and output transfer curve' })).toBeVisible();
  await expect(dialog.getByRole('slider', { name: 'Ratio' }).first()).toBeVisible();
  await page.keyboard.press('Escape');

  await addTemplateBlock(page, 'duck', 'duck');
  const duck = page.locator('.node[data-id="duck"]');
  await duck.getByRole('button', { name: 'Advanced', exact: true }).click();
  await expect(dialog.getByRole('group', { name: 'Sixteen step duck pattern' }).getByRole('button', { name: 'Step 1', exact: true })).toBeVisible();
  await expect(dialog.getByRole('img', { name: 'Duck gain envelope over one bar' })).toBeVisible();
});
