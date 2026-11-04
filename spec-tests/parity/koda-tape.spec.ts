import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// A muted Koda has tape over the mouth and does not dance; a press only asks whether the tape may come off, the second press takes it off and unmutes.
test.beforeEach(async ({ page }) => { await openApp(page); });

const state = (page) => page.evaluate(async () => (await import('/app/koda.js')).kodaState());
const svg = (page) => page.locator('#koda-layer .pethost svg').first();

test('a muted Koda wears tape, does not dance, and needs two presses to unmute', async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect.poll(async () => (await state(page)).presence).toBe('visible');
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(svg(page).locator('.mouthtape')).toBeHidden();
  await page.evaluate(async () => { (await import('/app/koda.js')).muteKoda(true); });
  await expect(svg(page).locator('.mouthtape')).toBeVisible();
  expect((await page.evaluate(async () => (await import('/app/koda-vibe.js')).vibeState())).active).toBe(false);
  const box = (await svg(page).boundingBox())!;
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await expect(svg(page)).toHaveClass(/tape-ask/);
  expect((await state(page)).muted).toBe(true);
  expect((await state(page)).bubble).toBeNull();
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
  await expect(svg(page)).toHaveClass(/tape-peel/);
  await expect.poll(async () => (await state(page)).muted).toBe(false);
  await expect(svg(page).locator('.mouthtape')).toBeHidden();
});
