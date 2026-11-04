import { test, expect, Page } from '@playwright/test';
import { emptySpot, openApp, state } from '../../web/tests/support';

// MUI03: the canvas on touch (phone project: 390x844, touch). Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

type Pt = { x: number; y: number };
async function cdp(page: Page) { return page.context().newCDPSession(page); }
/** Moves the view so the dot is at a comfortable spot of the canvas (the state's view plus a resize event, which redraws). */
async function bringTo(page: Page, selector: string) {
  await page.evaluate((sel) => {
    const r = document.querySelector(sel)!.getBoundingClientRect(), c = document.getElementById('cv')!.getBoundingClientRect();
    const v = (window as any).AMPLE_STATE.view;
    v.x += c.left + 120 - r.x; v.y += c.top + c.height / 2 - r.y;
    window.dispatchEvent(new Event('resize'));
  }, selector);
}
async function tapDot(page: Page, dot: import('@playwright/test').Locator) {
  const b = (await dot.boundingBox())!;
  await page.touchscreen.tap(b.x + b.width / 2, b.y + b.height / 2);
}
const pts = (list: Pt[]) => list.map((p, id) => ({ x: p.x, y: p.y, id }));

test('the hint talks about touch and does not overlap the zoom pill', async ({ page }) => {
  const hint = page.locator('#hint');
  await expect(hint).toContainText('pinch = zoom');
  await expect(hint).toContainText('long press = menu');
  await expect(hint).not.toContainText('wheel');
  const h = (await hint.boundingBox())!, z = (await page.locator('#zoomctl').boundingBox())!;
  expect(h.y + h.height).toBeLessThanOrEqual(z.y);
  expect(z.x + z.width).toBeLessThanOrEqual(390);
  expect(z.x).toBeGreaterThan(0);
  // the pill floats in the bottom right corner of the canvas
  const cv = (await page.locator('#cv').boundingBox())!;
  expect(z.y + z.height).toBeGreaterThan(cv.y + cv.height - 20);
  expect(z.x + z.width).toBeGreaterThan(380 - 4);
});

test('the minimap is hidden at first and the Map button brings it back', async ({ page }) => {
  await expect(page.locator('#mini')).toBeHidden();
  await expect(page.locator('#z-map')).toHaveAttribute('aria-pressed', 'false');
  await page.locator('#z-map').tap();
  await expect(page.locator('#mini')).toBeVisible();
  const m = (await page.locator('#mini').boundingBox())!, z = (await page.locator('#zoomctl').boundingBox())!;
  expect(m.y + m.height).toBeLessThanOrEqual(z.y);
});

test('one finger on the background pans, two fingers zoom', async ({ page }) => {
  const at = await emptySpot(page);
  const c = await cdp(page);
  const before = (await state(page)).view;
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pts([at]) });
  for (let i = 1; i <= 6; i++) await c.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: pts([{ x: at.x - i * 8, y: at.y }]) });
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await expect.poll(async () => (await state(page)).view.x).toBeLessThan(before.x - 30);

  const k0 = (await state(page)).view.k;
  const a = { x: at.x - 20, y: at.y }, b = { x: at.x + 20, y: at.y };
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pts([a, b]) });
  for (let i = 1; i <= 8; i++) await c.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: pts([{ x: a.x - i * 6, y: a.y }, { x: b.x + i * 6, y: b.y }]) });
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await expect.poll(async () => (await state(page)).view.k).toBeGreaterThan(k0 * 1.3);
});

test('a long press on the background opens the add-block menu and it stays open', async ({ page }) => {
  const at = await emptySpot(page);
  const c = await cdp(page);
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pts([at]) });
  await expect(page.locator('#menu')).toBeVisible({ timeout: 3000 });
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await page.waitForTimeout(300);
  await expect(page.locator('#menu')).toBeVisible();
  await expect(page.locator('#menu .hd').first()).toContainText('Add block here');
});

test('a long press on a block opens its block menu', async ({ page }) => {
  await page.locator('#z-fit').tap();
  const box = (await page.locator('.node').first().boundingBox())!;
  const at = { x: box.x + 8, y: box.y + 4 };
  const c = await cdp(page);
  await c.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: pts([at]) });
  await expect(page.locator('#menu')).toBeVisible({ timeout: 3000 });
  await c.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await expect(page.locator('#menu .hd').first()).toContainText(/v\d/);
});

test('a short tap does not open the menu', async ({ page }) => {
  const at = await emptySpot(page);
  await page.touchscreen.tap(at.x, at.y);
  await page.waitForTimeout(700);
  await expect(page.locator('#menu')).toBeHidden();
});

test('connector dots are bigger on touch', async ({ page }) => {
  const port = (await page.locator('.port').first().boundingBox())!;
  expect(port.width).toBeGreaterThanOrEqual(18 * (await state(page)).view.k - 1);
  expect(await page.locator('.port').first().evaluate((e) => getComputedStyle(e, '::after').width)).not.toBe('auto');
});

test('tap-tap connects: an output dot, then a compatible input dot', async ({ page }) => {
  await page.locator('#z-100').tap();
  // find an output dot and a compatible input dot of another block, and bring the pair into view
  const pair = await page.evaluate(() => {
    let best: { o: string; i: string; d: number } | null = null;
    for (const o of document.querySelectorAll<HTMLElement>('.port.out')) for (const i of document.querySelectorAll<HTMLElement>('.port.in')) {
      if (i.dataset.node === o.dataset.node || i.dataset.type !== o.dataset.type || i.dataset.type === 'any') continue;
      const a = o.getBoundingClientRect(), b = i.getBoundingClientRect(), d = Math.hypot(a.x - b.x, a.y - b.y);
      if (!best || d < best.d) best = { o: `.port.out[data-node="${o.dataset.node}"][data-idx="${o.dataset.idx}"]`, i: `.port.in[data-node="${i.dataset.node}"][data-idx="${i.dataset.idx}"]`, d };
    }
    return best;
  });
  expect(pair).not.toBeNull();
  const out = page.locator(pair!.o), inn = page.locator(pair!.i);
  // pan the world so both are on screen: fit shows everything
  await bringTo(page, pair!.o);
  await tapDot(page, out);
  await expect(out).toHaveClass(/chosen/);
  await expect(page.locator('#cv')).toHaveClass(/connecting/);
  await expect(inn).toHaveClass(/can/);
  await bringTo(page, pair!.i);
  await tapDot(page, inn);
  await expect(page.locator('#toast')).toContainText(/Connected|Types differ|cannot connect|Group ports/);
  await expect(page.locator('#cv')).not.toHaveClass(/connecting/);
});

test('tapping the chosen dot again cancels the choice', async ({ page }) => {
  await page.locator('#z-100').tap();
  await bringTo(page, '.port.out');
  await tapDot(page, page.locator('.port.out').first());
  await expect(page.locator('.port.out').first()).toHaveClass(/chosen/);
  await tapDot(page, page.locator('.port.out').first());
  await expect(page.locator('.port.out').first()).not.toHaveClass(/chosen/);
  await expect(page.locator('#cv')).not.toHaveClass(/connecting/);
});

test('tapping an input dot first asks for an output', async ({ page }) => {
  await page.locator('#z-100').tap();
  await bringTo(page, '.port.in');
  await tapDot(page, page.locator('.port.in').first());
  await expect(page.locator('#toast')).toContainText('Tap an output dot first');
});
