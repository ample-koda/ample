import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// MUI07: the header on the narrowest common phone (360 px wide), also with the longest play label. Run with --project=phone.
test.use({ viewport: { width: 360, height: 740 } });
test.beforeEach(async ({ page }) => { await openApp(page); });

const LABELS = ['▶', '▶ Build and play', '⏸ Pause', '■ Stop'];

test('at 360 px the header stays one row inside the screen with every play label, including "Build and play"', async ({ page }) => {
  for (const label of LABELS) {
    await page.locator('#btn-preview').evaluate((e, text) => { e.textContent = text; }, label);
    const r = await page.evaluate(() => {
      const h = document.querySelector('header')!.getBoundingClientRect();
      const out: { id: string; left: number; right: number; w: number; h: number }[] = [];
      for (const b of document.querySelectorAll<HTMLElement>('header button')) {
        if (!b.getClientRects().length) continue;
        const r = b.getBoundingClientRect();
        out.push({ id: b.id, left: r.left, right: r.right, w: r.width, h: r.height });
      }
      const play = document.getElementById('btn-preview')!;
      return { headerH: h.height, doc: document.documentElement.scrollWidth, out, clipped: play.scrollWidth > play.clientWidth + 1 };
    });
    expect(r.doc, label).toBeLessThanOrEqual(360);
    expect(r.headerH, label).toBeLessThan(62);
    for (const b of r.out) {
      expect(b.left, `${label} ${b.id}`).toBeGreaterThanOrEqual(0);
      expect(b.right, `${label} ${b.id}`).toBeLessThanOrEqual(360.5);
      expect(b.w, `${label} ${b.id}`).toBeGreaterThanOrEqual(43.5);
      expect(b.h, `${label} ${b.id}`).toBeGreaterThanOrEqual(43.5);
    }
    // the play label is readable: the text is not cut off by the button
    expect(r.clipped, `${label} clipped`).toBe(false);
    // no two buttons overlap
    const sorted = [...r.out].sort((a, b) => a.left - b.left);
    for (let i = 1; i < sorted.length; i++) expect(sorted[i].left, `${label} ${sorted[i].id}`).toBeGreaterThanOrEqual(sorted[i - 1].right - 0.5);
  }
  await expect(page.locator('#btn-more')).toBeVisible();
  await page.locator('#btn-more').tap();
  await expect(page.locator('#more-popup')).toBeVisible();
  const menu = (await page.locator('#more-popup').boundingBox())!;
  expect(menu.x).toBeGreaterThanOrEqual(0);
  expect(menu.x + menu.width).toBeLessThanOrEqual(360);
});
