import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// The title bar of an Advanced window on a phone: the window fits the screen and no button of the title bar is clipped.
freshServer();
test('the window fits a phone and every title bar button is inside the screen', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'm-comp');
  await node(page, 'm-comp').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const w = page.locator('#studio[open]');
  await expect(w).toBeVisible();
  const box = (await w.boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(-1); expect(box.x + box.width).toBeLessThanOrEqual(page.viewportSize()!.width + 1);
  for (const name of ['Effect enabled', 'Menu', 'Close advanced controls']) {
    const b = (await w.getByRole(name === 'Effect enabled' ? 'switch' : 'button', { name, exact: true }).boundingBox())!;
    expect(b, name).toBeTruthy();
    expect(b.x, name).toBeGreaterThanOrEqual(0); expect(b.x + b.width, name).toBeLessThanOrEqual(page.viewportSize()!.width + 1);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize()!.width);
});
