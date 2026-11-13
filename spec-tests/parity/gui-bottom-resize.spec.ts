import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The splitter above the bottom panel: the panel follows the drag, the canvas keeps a minimum height, nothing of the canvas hints or the tab row exists twice.
freshServer();

const tabs = ['mixer', 'arr', 'log', 'hist', 'project'];
for (const vp of [{ width: 1280, height: 720 }, { width: 2000, height: 1026 }]) {
  test(`resize the bottom panel with the live chain bar and the mixer tab at ${vp.width}x${vp.height}`, async ({ page }) => {
    await page.setViewportSize(vp);
    await openApp(page, { reset: false });
    await page.locator('#btabs button[data-t="mixer"]').click();
    // the live chain bar sits in the flow of the bottom panel
    await page.evaluate(() => { const b = document.createElement('section'); b.className = 'livechain'; b.textContent = 'Live chain'; b.style.height = '120px'; document.getElementById('bbody')!.before(b); });
        const grip = (await page.locator('#bgrip').boundingBox())!;
    const dragTo = async (y: number) => {
      const g = (await page.locator('#bgrip').boundingBox())!;
      await page.mouse.move(g.x + g.width / 2, g.y + 4); await page.mouse.down();
      await page.mouse.move(g.x + g.width / 2, y, { steps: 8 }); await page.mouse.up();
      await page.waitForTimeout(150);
    };
    const read = () => page.evaluate(() => {
      const r = (s: string) => document.querySelector(s)!.getBoundingClientRect();
      return { work: r('#work').height, bottom: r('#bottom').height, bottomTop: r('#bottom').top, bottomEnd: r('#bottom').bottom, sw: document.documentElement.scrollWidth, sh: document.documentElement.scrollHeight, bscroll: document.body.scrollTop + document.body.scrollLeft, ih: innerHeight };
    });
    for (const t of tabs) {
      if (t !== 'mixer') await page.locator(`#btabs button[data-t="${t}"]`).click();
      for (const y of [grip.y - 150, 0, vp.height, grip.y]) {
        await dragTo(y);
        const m = await read();
        expect(m.work, `canvas minimum at y=${y} tab ${t}`).toBeGreaterThanOrEqual(150);
        expect(m.bottomEnd).toBeLessThanOrEqual(m.ih + 1);
        expect(m.bscroll).toBe(0);
        expect(m.sh).toBeLessThanOrEqual(m.ih + 1);
        expect(await page.locator('#hint').count()).toBe(1);
        expect(await page.locator('#btabs').count()).toBe(1);
        expect(await page.locator('#zoomctl').count()).toBe(1);
      }
    }
    // the panel follows the drag
    await dragTo(500);
    const a = await read();
    await dragTo(450);
    const b = await read();
    expect(Math.abs(b.bottom - a.bottom - 50)).toBeLessThan(4);
    await page.screenshot({ path: `x/bottom-resize-${vp.width}.png` });
  });
}
