import { test, expect } from '@playwright/test';
import { bottomTab, node, openApp, state } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); await bottomTab(page, 'arr'); await page.evaluate(() => document.querySelector<HTMLElement>('#bottom')!.style.setProperty('--bh', '560px')); });

test('lanes are generated from the blocks and their parts', async ({ page }) => {
  await expect(page.locator('#arr .track')).toHaveCount(4);
  await expect(page.locator('#arr .bar[data-block="piano"]')).toHaveCount(2);
  await expect(page.locator('#arr .bar[data-block="piano"]').first()).toContainText('verse');
  await expect(page.locator('#arr .bar[data-block="drums"]')).toContainText('beat A');
  await expect(page.locator('#arr .ruler')).toContainText('bar 1');
});

test('labels stay fixed beside the horizontal timeline and the scrubber stays in its row', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 650 });
  const arr = page.locator('#arr');
  const label = page.locator('.arr-labels .tlabel').first();
  const clip = page.locator('#arr .bar').first();
  for (let i = 0; i < 4; i++) await page.locator('#arr-ctl button', { hasText: '+' }).click();
  await expect.poll(() => arr.evaluate((element) => element.scrollWidth > element.clientWidth)).toBe(true);
  const labelBefore = (await label.boundingBox())!, clipBefore = (await clip.boundingBox())!;
  await arr.evaluate((element) => { element.scrollLeft = 120; });
  await expect.poll(() => arr.evaluate((element) => element.scrollLeft)).toBe(120);
  const labelAfter = (await label.boundingBox())!, clipAfter = (await clip.boundingBox())!;
  expect(labelAfter.x).toBeCloseTo(labelBefore.x, 0);
  expect(clipAfter.x).toBeLessThan(clipBefore.x - 100);
  expect((await page.locator('.arr-labels').boundingBox())!.x + (await page.locator('.arr-labels').boundingBox())!.width).toBeLessThanOrEqual((await arr.boundingBox())!.x + 1);
  expect(await page.locator('#tp-wave').evaluate((element) => getComputedStyle(element).position)).toBe('relative');
  expect((await page.locator('#tp-wave').boundingBox())!.y + (await page.locator('#tp-wave').boundingBox())!.height).toBeLessThan((await page.locator('#arr .track').first().boundingBox())!.y);
  await page.evaluate(() => document.querySelector<HTMLElement>('#bottom')!.style.setProperty('--bh', '250px'));
  await expect.poll(() => arr.evaluate((element) => element.scrollHeight > element.clientHeight)).toBe(true);
  await arr.evaluate((element) => { element.scrollTop = 60; });
  await expect.poll(() => arr.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
  const lastTrack = (await page.locator('#arr .track').last().boundingBox())!, lastLabel = (await page.locator('.arr-labels .tlabel').last().boundingBox())!;
  expect(lastLabel.y).toBeCloseTo(lastTrack.y, 0);
});

test('clicking a clip selects its block and double-click opens exact timing', async ({ page }) => {
  await page.locator('#arr .bar[data-block="bass"]').click();
  expect((await state(page)).sel).toEqual(['bass']);
  await expect(node(page, 'bass')).toHaveClass(/sel/);
  await page.locator('#arr .bar[data-block="piano"]').first().dblclick();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#dlg-t')).toHaveText('Clip timing');
});

test('applying exact timing edits the song source and a context menu exposes timing and source actions', async ({ page }) => {
  await page.locator('#arr .bar[data-block="piano"]').first().dblclick();
  await page.locator('#dlg-extra input').nth(0).fill('3');
  await page.locator('#dlg-extra input').nth(1).fill('4');
  await page.locator('#dlg-btns button', { hasText: 'Apply timing' }).click();
  await page.locator('#dlg-btns button', { hasText: 'Keep block scope' }).click({ timeout: 3000 }).catch(() => {});   // the clip got shorter than the configured bars
  await expect.poll(async () => (await state(page)).text).toContain('at 3 for 4 as verse');
  await page.locator('#arr .bar[data-block="piano"]').first().click({ button: 'right' });
  await expect(page.locator('#menu')).toContainText('Edit timing');
  await expect(page.locator('#menu')).toContainText('Show in the source editor');
});

test('dragging a part moves its source placement', async ({ page }) => {
  const clip = page.locator('#arr .bar[data-block="piano"]').first();
  const r = (await clip.boundingBox())!;
  await page.mouse.move(r.x + r.width / 2, r.y + r.height / 2);
  await page.mouse.down();
  await page.mouse.move(r.x + r.width / 2 + 64, r.y + r.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect.poll(async () => (await state(page)).text).toContain('at 3 for 8 as verse');
});

test('zoom changes the width of the bars', async ({ page }) => {
  const before = (await page.locator('#arr .bar[data-block="drums"]').boundingBox())!.width;
  await page.locator('#arr-ctl button', { hasText: '+' }).click();
  const after = (await page.locator('#arr .bar[data-block="drums"]').boundingBox())!.width;
  expect(after).toBeGreaterThan(before);
});

test('dragging over the ruler chooses a bar range', async ({ page }) => {
  const r = (await page.locator('#arr .ruler').boundingBox())!;
  await page.mouse.move(r.x + 40, r.y + 8);
  await page.mouse.down();
  await page.mouse.move(r.x + 140, r.y + 8, { steps: 4 });
  await page.mouse.up();
  await expect(page.locator('#arr-preview')).toContainText('preview bars');
});
