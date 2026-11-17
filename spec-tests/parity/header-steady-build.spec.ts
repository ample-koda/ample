import { test, expect } from '@playwright/test';
import { freshServerPerTest, openApp } from '../../web/tests/support';

// The header keeps its height and its layout while a build starts and ends: Build and play becomes the play glyph and Stop becomes Cancel build, and the
// header used to wrap differently, so its rows changed height by a few pixels and the whole page shook at every build (owner report, 2026-11-17).
freshServerPerTest();

const song = `composition "Steady" { ample 1  tempo 120  master-seed 7 }
midi-clip theme at 1 for 2 {
  notes 1:0:1:60:100 1:1:1:64:100
}
synth lead {
  wave triangle
  from theme.notes -> notes
}
master out {
  from lead.audio -> audio
}
`;

for (const width of [1380, 1700]) {
  test(`at ${width}px the header does not move when a build starts and ends`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await openApp(page, { reset: false });
    await page.evaluate((t) => import('/app/store.js').then((s) => s.applyText(t)), song);
    const layout = () => page.evaluate(() => {
      const header = document.querySelector('header')!;
      const boxes = Array.from(header.children).map((e) => { const r = e.getBoundingClientRect(); return `${e.id || e.className}:${Math.round(r.left)},${Math.round(r.top)},${Math.round(r.height)}`; });
      return { height: Math.round(header.getBoundingClientRect().height), boxes: boxes.join(' ') };
    });
    const idle = await layout();
    await page.route('**/api/build', async (route) => { await new Promise((r) => setTimeout(r, 2000)); await route.continue(); });
    const running = page.evaluate(() => import('/app/store.js').then((s) => s.build()));
    await expect.poll(() => page.evaluate(() => import('/app/store.js').then((s) => s.S.build.running))).toBe(true);
    await page.waitForTimeout(200);
    const busy = await layout();
    await running;
    await page.waitForTimeout(300);
    const after = await layout();
    expect(busy.height, 'the header height while building').toBe(idle.height);
    expect(after.height, 'the header height after the build').toBe(idle.height);
    expect(busy.boxes, 'nothing in the header moves when the build starts').toBe(idle.boxes);
  });
}
