import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('mixer studio panel exposes four accessible gain and pan strips', async ({ page }) => {
  await openApp(page, { reset: false });
  const mixer = page.locator('.node[data-id="mix"]');
  await mixer.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog.getByRole('slider', { name: 'Input 1 gain' })).toBeVisible();
  await expect(dialog.getByRole('slider', { name: 'Input 4 pan' })).toBeVisible();
});
