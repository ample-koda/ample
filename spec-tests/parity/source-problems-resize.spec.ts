import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// The list of warnings under the source can be resized: drag the bar above it to show more or fewer; the list scrolls when there are more than fit.
const bad = Array.from({ length: 12 }, (_, i) => `  bad${i} 1\n`).join('');
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page);
  await page.evaluate(async (text) => { const { applyText } = await import('/app/store.js'); await applyText(text); }, `composition "c" { ample 1  tempo 100  master-seed 3 }\nview {\n${bad}}\n`);
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/bad11/);
});

test('dragging the bar above the warnings makes the list taller or shorter, and it keeps scrolling', async ({ page }) => {
  const list = page.locator('#se-prob');
  await expect.poll(async () => (await list.locator('> *').count())).toBeGreaterThan(5);
  const before = (await list.boundingBox())!.height;
  expect(await list.evaluate((n) => n.scrollHeight > n.clientHeight)).toBe(true);
  const grip = page.locator('.seprob-grip');
  const box = (await grip.boundingBox())!;
  await page.mouse.move(box.x + 40, box.y + 3);
  await page.mouse.down();
  await page.mouse.move(box.x + 40, box.y - 90, { steps: 6 });
  await page.mouse.up();
  const taller = (await list.boundingBox())!.height;
  expect(taller).toBeGreaterThan(before + 60);
  const gripBox = (await grip.boundingBox())!;
  await page.mouse.move(gripBox.x + 40, gripBox.y + 3);
  await page.mouse.down();
  await page.mouse.move(gripBox.x + 40, gripBox.y + 150, { steps: 6 });
  await page.mouse.up();
  expect((await list.boundingBox())!.height).toBeLessThan(taller - 60);
});
