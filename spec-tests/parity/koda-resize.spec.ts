import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Koda can be resized (0.5 to 1.25 in steps of 0.05, default 1, 0.8 on a phone), kept in this browser only.
freshServer();
// Koda settles into its place after it opens: a mouse that goes to where it was a moment ago misses it on a slow machine
const still = async (page) => { let last = ''; await expect.poll(async () => { const now = JSON.stringify(await page.locator('#koda-float').boundingBox()); const same = now === last; last = now; return same; }, { intervals: [250, 250, 250], timeout: 15000 }).toBe(true); };
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.locator('#btn-koda').click(); await expect(page.locator('#koda-layer')).toBeVisible(); await still(page); });

const width = (page) => page.locator('#koda-float').evaluate((e) => Math.round(e.getBoundingClientRect().width));

test('the corners scale the whole of Koda in steps of 0.05 and the size is kept', async ({ page }) => {
  expect(await width(page)).toBe(150);   // the default
  const box = (await page.locator('#koda-float').boundingBox())!;
  await page.locator('#koda-float svg.pet').click({ button: 'right' });
  await page.getByText('Resize…', { exact: true }).click();
  await expect(page.locator('.k-resize-handle')).toHaveCount(4);
  await still(page);
  const handle = (await page.locator('.k-resize-handle.tl').boundingBox())!;
  await page.mouse.move(handle.x + 10, handle.y + 10);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.45, box.y + box.height * 0.45, { steps: 6 });   // towards the opposite corner: smaller
  await page.mouse.up();
  const stored = Number(await page.evaluate(() => localStorage.getItem('ample.koda.scale')));
  expect(stored).toBeGreaterThanOrEqual(0.5);
  expect(stored).toBeLessThan(1);
  expect(Math.round(stored * 20)).toBeCloseTo(stored * 20, 6);   // a multiple of 0.05
  await page.locator('.k-resize-done').click();
  await expect(page.locator('.k-resize-handle')).toHaveCount(0);
  expect(await width(page)).toBeLessThan(150);
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  expect(await width(page)).toBeLessThan(150);   // kept in this browser
});

test('on a phone Koda starts smaller (0.8) unless a size was chosen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  expect(await width(page)).toBe(88);   // 110 x 0.8
});

test('the resize frame follows Koda when it is dragged before Done', async ({ page }) => {
  await page.locator('#koda-float svg.pet').click({ button: 'right' });
  await page.getByText('Resize…', { exact: true }).click();
  await still(page);
  const before = (await page.locator('.k-resize').boundingBox())!;
  const pet = (await page.locator('#koda-float svg.pet').boundingBox())!;
  await page.mouse.move(pet.x + pet.width / 2, pet.y + pet.height / 2);
  await page.mouse.down();
  await page.mouse.move(pet.x + pet.width / 2 - 120, pet.y + pet.height / 2 - 90, { steps: 8 });
  await page.mouse.up();
  await expect.poll(async () => Math.round((await page.locator('.k-resize').boundingBox())!.x)).toBeLessThan(Math.round(before.x) - 60);
});
