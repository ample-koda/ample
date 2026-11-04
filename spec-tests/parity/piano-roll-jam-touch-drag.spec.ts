import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServerPerTest, node, openApp } from '../../web/tests/support';

// The AMPLE Jam window can be dragged by its title with a finger (a touchscreen, also over a remote desktop): the browser must hand the touch to the page and not scroll with it.
freshServerPerTest();
test.use({ hasTouch: true, viewport: { width: 1400, height: 900 } });
test.beforeEach(async ({ page }) => { await openApp(page); });

test('a finger drag on the title of the Jam window moves the window', async ({ page }) => {
  await addTemplateBlock(page, 'midi-clip', 'tdrag');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'tdrag').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  const title = panel.locator('.mls-title');
  const box = (await title.boundingBox())!;
  const before = (await panel.boundingBox())!;
  const client = await page.context().newCDPSession(page);
  const x = box.x + box.width / 2, y = box.y + box.height / 2;
  const touch = (type: string, px: number, py: number) => client.send('Input.dispatchTouchEvent', { type, touchPoints: type === 'touchEnd' ? [] : [{ x: px, y: py, id: 1 }] });
  await touch('touchStart', x, y);
  for (let i = 1; i <= 8; i++) await touch('touchMove', x + i * 15, y + i * 10);
  await touch('touchEnd', x + 120, y + 80);
  await expect.poll(async () => Math.round(((await panel.boundingBox())!).x - before.x)).toBeGreaterThan(80);
  expect(Math.round(((await panel.boundingBox())!).y - before.y)).toBeGreaterThan(50);
});
