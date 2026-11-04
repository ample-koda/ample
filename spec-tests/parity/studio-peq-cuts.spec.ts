import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('a peq band can be a low cut or a high cut and can be switched off', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'peq', 'peq');
  await page.locator('.node[data-id="peq"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  const type = dialog.getByRole('combobox', { name: 'Band 1 type' });
  await expect(type.locator('option')).toContainText(['Low cut', 'High cut']);
  await type.selectOption('lowcut');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/band1_type lowcut/);
  const toggle = dialog.getByRole('button', { name: 'Band 3 enabled' });
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).text, { timeout: 10000 }).toMatch(/band3_enabled off/);
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-pressed', 'true');
});
