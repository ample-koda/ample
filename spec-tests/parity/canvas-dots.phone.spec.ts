import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// MUI07: the canvas (#cv) was hidden once after repeated taps on connector dots (MUI03 note). Cause: two quick taps on dots of one block are a double
// click, and a double click on a block opens its source view, which hides #cv (#canvaswrap.bmode). A tap that Chromium moved from a dot to the
// neighbouring </> button did the same. Both are fixed; this keeps them fixed. Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

test('tapping every visible connector dot quickly one after the other never hides the canvas', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).tap();
  const pts: { x: number; y: number }[] = await page.evaluate(() => [...document.querySelectorAll('.port')].map((p) => { const r = p.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; }).filter((p) => p.x > 0 && p.x < 390 && p.y > 60 && p.y < 600));
  expect(pts.length).toBeGreaterThan(8);
  for (const p of pts) {
    await page.touchscreen.tap(p.x, p.y);
    await page.waitForTimeout(100);
    expect(await page.evaluate(() => document.getElementById('canvaswrap')!.classList.contains('bmode'))).toBe(false);
  }
  await expect(page.locator('#cv')).toBeVisible();
});

test('a double click that lands on a connector dot does not open the source view; on the block itself it still does', async ({ page }) => {
  await page.locator('#z-100').tap();
  const port = page.locator('.node .port.out').first();
  await port.evaluate((e) => e.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true })));
  await page.waitForTimeout(200);
  await expect(page.locator('#canvaswrap')).not.toHaveClass(/bmode/);
  await expect(page.locator('#cv')).toBeVisible();
  await page.locator('.node').first().evaluate((e) => e.dispatchEvent(new MouseEvent('dblclick', { bubbles: true, cancelable: true })));
  await expect(page.locator('#canvaswrap')).toHaveClass(/bmode/);
});
