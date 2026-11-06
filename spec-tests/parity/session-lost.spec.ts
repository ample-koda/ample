import { test, expect } from '@playwright/test';
import { freshServer, launchUrl } from '../../web/tests/support';

// A tab whose session token is wrong (the server restarted, the sign-in expired): on a server without a sign-in the studio shows ONE information box and no toast, and the loading overlay is gone.
freshServer();

test('a rejected session shows one box and no toast, and the loading overlay does not stay', async ({ page }) => {
  await page.addInitScript(() => { sessionStorage.setItem('ample.token', 'deadbeef'); });
  await page.goto(launchUrl().split('#')[0]);
  await expect(page.locator('#session-warning[open]')).toBeVisible({ timeout: 15000 });
  await expect(page.locator('#boot')).toHaveCount(0);
  await expect(page.locator('#toast.on')).toHaveCount(0);
});
