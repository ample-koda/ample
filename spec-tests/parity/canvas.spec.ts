import { test, expect } from '@playwright/test';
import { chooseMenu, emptySpot, node, openApp, state } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

test('the wheel zooms about the pointer and the buttons zoom, fit and reset', async ({ page }) => {
  const before = (await state(page)).view.k;
  await page.locator('#cv').hover({ position: { x: 400, y: 300 } });
  await page.mouse.wheel(0, -300);
  await expect.poll(async () => (await state(page)).view.k).toBeGreaterThan(before);
  await page.click('#z-100');
  expect((await state(page)).view.k).toBe(1);
  await expect(page.locator('#z-pct')).toHaveText('100%');
  await page.click('#z-fit');
  const k = (await state(page)).view.k;
  expect(k).toBeGreaterThanOrEqual(0.2);
  expect(k).toBeLessThanOrEqual(1.5);
});

test('canvas context menu exposes Fit under Layout', async ({ page }) => {
  const at = await emptySpot(page);
  await page.mouse.click(at.x, at.y, { button: 'right' });
  await chooseMenu(page, 'Layout', 'Fit');
  await expect(page.locator('#menu')).toBeHidden();
});

test('dragging the background pans', async ({ page }) => {
  const before = (await state(page)).view;
  const at = await emptySpot(page);
  await page.mouse.move(at.x, at.y);
  await page.mouse.down();
  await page.mouse.move(at.x + 60, at.y + 40, { steps: 4 });
  await page.mouse.up();
  const after = (await state(page)).view;
  expect(after.x - before.x).toBeCloseTo(60, 0);
  expect(after.y - before.y).toBeCloseTo(40, 0);
});

test('click selects, ctrl+click adds, shift+drag selects with a rectangle, ctrl+A selects all', async ({ page }) => {
  await node(page, 'chords').locator('.nh .t').click();
  expect((await state(page)).sel).toEqual(['chords']);
  await node(page, 'bass').locator('.nh .t').click({ modifiers: ['Control'] });
  expect((await state(page)).sel.sort()).toEqual(['bass', 'chords']);
  const at = await emptySpot(page);
  await page.mouse.click(at.x, at.y);
  expect((await state(page)).sel).toEqual([]);
  await page.keyboard.press('Control+a');
  expect((await state(page)).sel.length).toBe(6);
  await page.mouse.click(at.x, at.y);
  const box = (await page.locator('#cv').boundingBox())!;
  await page.keyboard.down('Shift');
  await page.mouse.move(box.x + 5, box.y + 5);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 5, box.y + box.height - 5, { steps: 4 });
  await page.mouse.up();
  await page.keyboard.up('Shift');
  expect((await state(page)).sel.length).toBeGreaterThan(1);
});

test('moving a block snaps to the grid and writes a layout line to the source', async ({ page }) => {
  const head = node(page, 'drums').locator('.nh .t');
  const box = (await head.boundingBox())!;
  await page.mouse.move(box.x + 40, box.y + 10);
  await page.mouse.down();
  await page.mouse.move(box.x + 97, box.y + 63, { steps: 5 });
  await page.mouse.up();
  await expect.poll(async () => (await state(page)).text).toMatch(/layout drums -?\d+0 -?\d+0/);
});

test('the minimap shows the view and moves it', async ({ page }) => {
  await expect(page.locator('#mini')).toBeVisible();
  const before = (await state(page)).view;
  const box = (await page.locator('#mini').boundingBox())!;
  await page.mouse.click(box.x + 20, box.y + 20);
  const after = (await state(page)).view;
  expect(after.x !== before.x || after.y !== before.y).toBe(true);
  await page.click('#z-map');
  await expect(page.locator('#mini')).toBeHidden();
});

test('coming back to the tab does not redraw the blocks when nothing changed (no blinking players)', async ({ page }) => {
  await page.locator('.node').first().waitFor();
  await page.evaluate(async () => { const store = await import('/app/store.js'); await store.loadHistory(); });
  await page.waitForTimeout(500);
  await page.evaluate(() => { document.querySelectorAll('.node').forEach((n) => ((n as any).__kept = true)); });
  // what the reconnect of the event stream runs when the tab is visible again
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.reloadDoc({ ifChanged: true });
    await store.loadHistory();
    await store.loadHistory();
  });
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => [...document.querySelectorAll('.node')].every((n) => (n as any).__kept))).toBe(true);
});
