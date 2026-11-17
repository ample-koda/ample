import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// The transport of an Advanced window is a compact toolbar row at the top of the body (Play, Loop, A/B, Compare / render), and nothing at the bottom is cut off.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 800 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 't-comp');
  await node(page, 't-comp').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('#studio[open]')).toBeVisible();
});

test('the toolbar sits under the title bar, above the controls, with A/B in it and not in the header', async ({ page }) => {
  const w = page.locator('#studio[open]');
  const bar = (await w.locator('.studio-preview-slot').boundingBox())!, body = (await w.locator('.studio-dialog-body').boundingBox())!, head = (await w.locator('.studio-dialog-header').boundingBox())!;
  expect(bar.y).toBeGreaterThanOrEqual(head.y + head.height - 1);
  expect(bar.y + bar.height).toBeLessThanOrEqual(body.y + 1);
  await expect(w.locator('.studio-dialog-header .studio-ab')).toHaveCount(0);
  await expect(w.locator('.studio-preview-slot .studio-ab')).toHaveCount(1);
});

test('the compressor window fits on a 800 px screen: the body scrolls, the toolbar and the window edge stay visible', async ({ page }) => {
  const w = page.locator('#studio[open]');
  const box = (await w.boundingBox())!;
  expect(box.y + box.height).toBeLessThanOrEqual(800);
  await expect(w.locator('.studio-preview-slot')).toBeVisible();
  expect(await w.locator('.studio-dialog-body').evaluate((el) => getComputedStyle(el).overflowY)).toBe('auto');
});
