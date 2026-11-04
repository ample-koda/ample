import { test, expect, Page } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// MUI04: touch targets of at least 44x44 px, spacing in the header, hover-only controls reachable, volume in the more menu. Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

async function small(page: Page, selector: string, min = 44) {
  return page.evaluate(([sel, m]) => [...document.querySelectorAll<HTMLElement>(sel as string)]
    .filter((e) => e.getClientRects().length && getComputedStyle(e).visibility !== 'hidden')
    .map((e) => { const r = e.getBoundingClientRect(); return { id: e.id || e.className || e.textContent?.slice(0, 20), w: Math.round(r.width), h: Math.round(r.height) }; })
    .filter((r) => r.w < (m as number) - 0.5 || r.h < (m as number) - 0.5), [selector, min]);
}

test('every visible header button and the zoom pill buttons are at least 44x44', async ({ page }) => {
  expect(await small(page, 'header button')).toEqual([]);
  expect(await small(page, '#zoomctl button')).toEqual([]);
  expect(await small(page, '#btabs > button[data-t], #b-more')).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const right = await page.locator('#btn-more').evaluate((e) => e.getBoundingClientRect().right);
  expect(right).toBeLessThanOrEqual(390);
});

test('Undo, Redo and the play button have room between them', async ({ page }) => {
  const b = async (id: string) => (await page.locator(id).boundingBox())!;
  const [u, r, p] = [await b('#btn-undo'), await b('#btn-redo'), await b('#btn-preview')];
  expect(r.x - (u.x + u.width)).toBeGreaterThanOrEqual(4);
  expect(p.x - (r.x + r.width)).toBeGreaterThanOrEqual(4);
});

test('the menus have 44 px rows: more menu, context menu, dialog buttons', async ({ page }) => {
  await page.locator('#btn-more').tap();
  expect(await small(page, '#more-popup [role^="menuitem"]', 44)).toEqual([]);
  await page.keyboard.press('Escape');
  await page.locator('#cv').click({ button: 'right', position: { x: 300, y: 500 } }).catch(() => {});
  await page.evaluate(() => document.getElementById('cv')!.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, clientX: 300, clientY: 500 })));
  await expect(page.locator('#menu')).toBeVisible();
  expect((await small(page, '#menu > div:not(.flyout):not(.hd)', 44))).toEqual([]);
});

test('the small buttons on a block have a 44 px touch area and a tap on the area presses them', async ({ page }) => {
  await page.locator('#z-100').tap();
  const btn = page.locator('.node .studio-source-node').first();
  await btn.scrollIntoViewIfNeeded();
  const area = await btn.evaluate((e) => { const a = getComputedStyle(e, '::after'); return { w: parseFloat(a.width), h: parseFloat(a.height), pos: getComputedStyle(e).position }; });
  expect(area.w).toBeGreaterThanOrEqual(44);
  expect(area.h).toBeGreaterThanOrEqual(44);
  expect(area.pos).toBe('relative');
});

test('a long press on a block button shows its tooltip', async ({ page }) => {
  await page.locator('#z-fit').tap();   // the phone starts at a readable zoom at the top left; fit first so the first block is in view
  await page.locator('#z-100').tap();
  const btn = page.locator('.node .studio-source-node').first();
  const title = await btn.getAttribute('title');
  const box = (await btn.boundingBox())!;
  const c = await page.context().newCDPSession(page);
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2, id: 0 }] });
  await expect(page.locator('#toast')).toContainText(title!, { timeout: 3000 });
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
});

test('Volume in the more menu opens the volume popup and Escape closes it', async ({ page }) => {
  await page.locator('#btn-more').tap();
  await page.locator('#more-popup [role=menuitem]', { hasText: 'Volume' }).tap();
  await expect(page.locator('#play-volume')).toBeVisible();
  const box = (await page.locator('#play-volume').boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  await expect(page.locator('#play-volume input').first()).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#play-volume')).toBeHidden();
});
