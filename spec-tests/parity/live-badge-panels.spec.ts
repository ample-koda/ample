import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

// Panels that draw their own controls (peq graph, mixer strips, volume faders) carry the live / re-renders / needs-build badge too, as a strip under the panel.
freshServer();
for (const [type, expectedLive] of [['peq', 'live: 30 parameters'], ['volume', 'live: ']] as const) {
  test(`the ${type} panel says what the live preview does with its parameters`, async ({ page }) => {
    await openApp(page, { reset: false });
    await addTemplateBlock(page, type, `b-${type}`);
    await page.locator(`.node[data-id="b-${type}"]`).getByRole('button', { name: 'Advanced', exact: true }).click();
    const strip = page.locator('#studio .live-badge-summary');
    await expect(strip).toBeVisible();
    await expect(strip.locator('.live-param').first()).toContainText(expectedLive);
  });
}
