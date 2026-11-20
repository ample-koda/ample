import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The Mixer on a phone: the tab is not in the bar (mobile.css), it opens from "More", and the two send knobs of an input do not lie on top of each other
// (the 44 px touch size of every button in the panel made them wider than the strip, 50 px).
freshServer();

test('on a phone the Mixer opens from More and the send knobs of an input do not overlap', async ({ page }) => {
  await page.setViewportSize({ width: 420, height: 860 });
  await openApp(page, { reset: false });
  await expect(page.locator('#btabs button[data-t="mixer"]')).toBeHidden();
  await page.locator('#b-more').click();
  await page.locator('#more-tabs button', { hasText: 'Mixer' }).click();
  await expect(page.locator('#b-mixer .mixview')).toBeVisible();
  const lane = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan').first();
  const sends = lane.locator('.mixsend');
  await expect(sends).toHaveCount(2);
  const [box, a, b] = await Promise.all([lane.boundingBox(), sends.nth(0).boundingBox(), sends.nth(1).boundingBox()]);
  expect(box && a && b).toBeTruthy();
  expect(a!.x + a!.width, 'the reverb knob ends where the delay knob starts').toBeLessThanOrEqual(b!.x + 0.5);
  expect(a!.x, 'both knobs lie inside their own strip, not over the next one').toBeGreaterThanOrEqual(box!.x - 0.5);
  expect(b!.x + b!.width).toBeLessThanOrEqual(box!.x + box!.width + 0.5);
});
