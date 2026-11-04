import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The Let's go button must take a click at once, wherever on it the pointer is (its edges too), also while it wiggles under the pointer.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

async function showPatchDraft(page) {
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__kept = 0;
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
      run: async () => ({ kind: 'draft', summary: [{ type: 'change', text: 'a one' }, { type: 'add', text: 'b two' }], apply: async () => { (window as any).__kept++; return true; } }),
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('make it so');
  await page.keyboard.press('Enter');
  const button = page.locator('#koda-layer button.letsgo');
  await expect(button).toBeVisible({ timeout: 10000 });
  return button;
}

for (const where of ['center', 'bottom edge', 'top edge', 'left edge', 'right edge']) {
  test(`one press on the ${where} of Let's go keeps the draft`, async ({ page }) => {
    const button = await showPatchDraft(page);
    await page.waitForTimeout(700);   // the pop-in of the bubble is over
    const b = (await button.boundingBox())!;
    const at = { center: [b.width / 2, b.height / 2], 'bottom edge': [b.width / 2, b.height - 2], 'top edge': [b.width / 2, 2], 'left edge': [3, b.height / 2], 'right edge': [b.width - 3, b.height / 2] }[where] as number[];
    await page.mouse.move(b.x + at[0], b.y + at[1], { steps: 4 });
    await page.waitForTimeout(250);   // hovering: the glow and the wiggle have started
    await page.mouse.down();
    await page.waitForTimeout(90);   // a human press lasts a moment
    await page.mouse.up();
    await expect.poll(() => page.evaluate(() => (window as any).__kept), { timeout: 3000 }).toBe(1);
  });
}
