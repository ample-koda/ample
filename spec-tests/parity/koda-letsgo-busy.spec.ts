import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The Let's go button is busy while its click is handled, and works again when the draft is still there afterwards (the patch was refused or failed): it was dead for good.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

test("Let's go works again when the patch is refused and the draft stays", async ({ page }) => {
  await page.locator('#btn-koda').click();
  await expect(page.locator('#koda-layer .bub')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.evaluate(async () => {
    const { setBackend } = await import('/app/koda-backend.js');
    (window as any).__tries = 0;
    setBackend({
      explain: async () => ({ kind: 'answer', title: 'x', text: 'y' }),
      run: async () => ({ kind: 'draft', summary: [{ type: 'change', text: 'a one' }, { type: 'add', text: 'b two' }], apply: async () => { (window as any).__tries++; throw new Error('refused'); } }),
    });
  });
  await page.locator('.node').first().click();
  await page.keyboard.press('Control+k');
  await page.locator('#koda-layer .bub.k-menu').getByRole('button', { name: 'Do this…' }).click();
  await page.locator('#koda-layer .bub.k-choice input').fill('make it so');
  await page.keyboard.press('Enter');
  const button = page.locator('#koda-layer button.letsgo');
  await expect(button).toBeVisible({ timeout: 10000 });
  await page.waitForTimeout(700);   // the pop-in of the bubble is over
  await button.evaluate((b: HTMLElement) => b.click());   // the button wiggles: a click on the element itself
  await expect.poll(() => page.evaluate(() => (window as any).__tries), { timeout: 5000 }).toBeGreaterThanOrEqual(1);
  // the draft is still on the screen after the refusal: the button is not busy for ever and takes a second click
  await expect(page.locator('#koda-layer button.letsgo')).not.toHaveAttribute('aria-busy', 'true', { timeout: 14000 });
  await page.locator('#koda-layer button.letsgo').evaluate((b: HTMLElement) => b.click());
  await expect.poll(() => page.evaluate(() => (window as any).__tries), { timeout: 5000 }).toBeGreaterThanOrEqual(2);
});
