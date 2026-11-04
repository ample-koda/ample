import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('the filter panel has a slope selector from 12 to 48 dB per octave and writes the choice', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'filter', 'filter');
  await page.locator('.node[data-id="filter"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const slope = dialog.getByRole('combobox', { name: 'Filter slope' });
  await expect(slope).toBeVisible();
  await expect(slope.locator('option')).toHaveText(['12 dB/oct', '24 dB/oct', '36 dB/oct', '48 dB/oct']);
  await slope.selectOption('48');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/slope 48/);
  // the response curve is drawn for the steeper slope (a path with a d attribute)
  await expect(dialog.locator('.studio-filter-panel .studio-response-line')).toHaveAttribute('d', /^M/);
});
