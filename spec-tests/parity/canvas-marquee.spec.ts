import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The selection rectangle: Shift and drag, or (touch and mouse) a tap or click on the empty canvas followed at once by a press that is held and dragged.
freshServer();
test('a click on the empty canvas and then at once a held press with a drag draws the selection rectangle, without Shift', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  const nl = String.fromCharCode(10);
  await page.locator('#se-cm .cm-content').fill(['composition "demo" { ample 1  tempo 96  master-seed 424242 }', 'import-audio rain at 1 for 8 {', '  file assets/missing.wav', '}', 'volume level at 1 for 8 {', '  from rain.audio -> audio', '}', ''].join(nl));
  await page.click('#vs-blocks');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();   // the text was just replaced: fit the view so that both blocks are on screen (without it they could lie outside the canvas)
  await expect(page.locator('.node[data-id="level"]')).toBeVisible();
  await expect(page.locator('.node[data-id="rain"]')).toBeVisible();
  await page.waitForTimeout(300);   // the view animates to its fit
  const a = await page.locator('.node[data-id="rain"]').boundingBox();
  const b = await page.locator('.node[data-id="level"]').boundingBox();
  const left = Math.min(a!.x, b!.x) - 10, top = Math.min(a!.y, b!.y) - 10;
  const right = Math.max(a!.x + a!.width, b!.x + b!.width) + 10, bottom = Math.max(a!.y + a!.height, b!.y + b!.height) + 10;
  const size = () => page.evaluate(async () => (await import('/app/store.js')).S.selSet.size);
  expect(await size()).toBeLessThan(2);
  await page.mouse.click(left, top);   // a click on the empty canvas (the top left: the bottom right of a fitted view can lie under the minimap)
  await page.mouse.move(left, top);
  await page.mouse.down();   // pressed again at once and held
  await page.mouse.move((left + right) / 2, (top + bottom) / 2, { steps: 4 });
  await page.mouse.move(right, bottom, { steps: 4 });
  await page.mouse.up();
  expect(await size()).toBeGreaterThanOrEqual(2);
});

test('dragging one of several selected blocks moves them all; a plain click on it selects just that block', async ({ page }) => {
  await openApp(page); await page.click('#vs-source'); await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  const nl = String.fromCharCode(10);
  await page.locator('#se-cm .cm-content').fill(['composition "demo" { ample 1  tempo 96  master-seed 424242 }', 'import-audio rain at 1 for 8 {', '  file assets/missing.wav', '}', 'volume level at 1 for 8 {', '  from rain.audio -> audio', '}', ''].join(nl));
  await page.click('#vs-blocks');
  const rain = page.locator('.node[data-id="rain"]'), level = page.locator('.node[data-id="level"]');
  await expect(level).toBeVisible();
  await page.waitForTimeout(1500);   // the song is saved and read back first, which would clear a selection
  await page.evaluate(async () => { const s = await import('/app/store.js'); s.select('rain'); s.select('level', true); s.notifySelection(); s.notify(); });
  await page.waitForTimeout(400);
  const size = () => page.evaluate(async () => (await import('/app/store.js')).S.selSet.size);
  expect(await size()).toBe(2);
  const a = await rain.boundingBox(), b = await level.boundingBox();
  await page.evaluate(() => { (document.querySelector('.node[data-id="rain"]') as any).__kept = 1; });
  const h = await level.locator('.t').first().boundingBox();
  await page.mouse.move(h!.x + 10, h!.y + 5);
  await page.mouse.down();
  await page.mouse.move(h!.x + 10 + 60, h!.y + 5 + 40, { steps: 5 });
  await page.mouse.up();
  const a2 = await rain.boundingBox(), b2 = await level.boundingBox();
  expect(Math.round(a2!.x - a!.x)).toBeGreaterThan(40);
  expect(Math.round(b2!.x - b!.x)).toBeGreaterThan(40);
  expect(await size()).toBe(2);
  await page.waitForTimeout(500);
  // moving blocks does not draw them again (their waveforms stay)
  expect(await page.evaluate(() => (document.querySelector('.node[data-id="rain"]') as any).__kept)).toBe(1);
  await level.locator('.t').first().click();
  expect(await size()).toBe(1);
});
