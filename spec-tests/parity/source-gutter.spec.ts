import { test, expect } from '@playwright/test';
import { freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// The numbers of the source editor stick to the left while long lines scroll under them (a wide screen, touch scroll): the column must be opaque, or the text shows through the numbers.
freshServer();

test('the line numbers stay opaque and in place when a long line is scrolled sideways', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const long = 'x'.repeat(400);
  await setSrc(page, ['composition "p" { ample 1  tempo 100  master-seed 3 }', '', `# ${long}`, '# short', ''].join('\n'));
  const gutters = page.locator('.cm-gutters').first();
  await expect(gutters).toBeVisible();
  expect(await gutters.evaluate((e) => getComputedStyle(e).backgroundColor)).not.toMatch(/rgba\(0, 0, 0, 0\)|transparent/);
  const before = await gutters.boundingBox();
  await page.locator('.cm-scroller').first().evaluate((e) => { e.scrollLeft = 200; });
  await expect.poll(() => page.locator('.cm-scroller').first().evaluate((e) => e.scrollLeft)).toBeGreaterThan(100);
  const after = await gutters.boundingBox();
  expect(Math.abs((after?.x ?? 0) - (before?.x ?? 0))).toBeLessThan(2);   // the numbers did not move with the text
});
