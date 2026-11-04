import { test, expect } from '@playwright/test';
import { openApp, srcText } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

test('the three panels can be hidden and shown', async ({ page }) => {
  await expect(page.locator('#left')).toBeVisible();
  await page.click('#tg-left');
  await expect(page.locator('#left')).toBeHidden();
  await page.click('#tg-right');
  await expect(page.locator('#right')).toBeHidden();
  await page.click('#tg-bottom');
  await expect(page.locator('#bbody')).toBeHidden();
  await page.click('#tg-left');
  await expect(page.locator('#left')).toBeVisible();
});

test('the bottom panel resizes and the layout survives a reload', async ({ page }) => {
  const before = (await page.locator('#bottom').boundingBox())!.height;
  const grip = (await page.locator('#bgrip').boundingBox())!;
  await page.mouse.move(grip.x + 100, grip.y + 2);
  await page.mouse.down();
  await page.mouse.move(grip.x + 100, grip.y - 80, { steps: 4 });
  await page.mouse.up();
  const resized = (await page.locator('#bottom').boundingBox())!.height;
  expect(resized).toBeGreaterThan(before + 40);
  await page.click('#tg-left');
  await page.reload();
  await expect(page.locator('.node').first()).toBeVisible();
  await expect(page.locator('#left')).toBeHidden();
  expect(Math.abs((await page.locator('#bottom').boundingBox())!.height - resized)).toBeLessThan(3);
});

test('dragging the arrangement panel updates its height without repeated window resize events', async ({ page }) => {
  await expect(page.locator('#arr')).toBeVisible();
  await page.evaluate(() => { (window as any).layoutResizeEvents = 0; window.addEventListener('resize', () => (window as any).layoutResizeEvents++); });
  const before = (await page.locator('#bottom').boundingBox())!.height;
  const grip = (await page.locator('#bgrip').boundingBox())!;
  await page.mouse.move(grip.x + 100, grip.y + 2);
  await page.mouse.down();
  await page.mouse.move(grip.x + 100, grip.y - 100, { steps: 20 });
  await page.mouse.up();
  expect((await page.locator('#bottom').boundingBox())!.height).toBeGreaterThan(before + 60);
  expect(await page.evaluate(() => (window as any).layoutResizeEvents)).toBe(1);
});

test('the side panels resize with their handles', async ({ page }) => {
  const before = (await page.locator('#left').boundingBox())!.width;
  const g = (await page.locator('#lgrip').boundingBox())!;
  await page.mouse.move(g.x + 2, g.y + 100);
  await page.mouse.down();
  await page.mouse.move(g.x + 82, g.y + 100, { steps: 4 });
  await page.mouse.up();
  expect((await page.locator('#left').boundingBox())!.width).toBeGreaterThan(before + 50);
});

test('the source view shows the song (the source is a view of the canvas, no longer a bottom tab)', async ({ page }) => {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toContain('master master');
});
