import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// MUI02: bottom navigation with four fixed tabs plus More; the tab panel is a bottom sheet. Run with --project=phone.
test.beforeEach(async ({ page }) => { await openApp(page); });

test('four tabs and More are fixed at the bottom, the sheet starts closed', async ({ page }) => {
  const tabs = page.locator('#btabs button[data-t]:visible');
  expect(await tabs.evaluateAll((l) => l.map((b) => (b as HTMLElement).dataset.t))).toEqual(['arr', 'branch', 'hist', 'project']);
  await expect(page.locator('#b-more')).toBeVisible();
  await expect(page.locator('#bbody')).toBeHidden();
  const nav = (await page.locator('#btabs').boundingBox())!;
  expect(nav.y + nav.height).toBeGreaterThan(844 - 4);
  expect(nav.width).toBeGreaterThan(380);
  for (const b of await tabs.all()) expect((await b.boundingBox())!.height).toBeGreaterThanOrEqual(44);
});

test('tapping a tab opens its panel as a sheet with a drag handle, tapping it again closes it', async ({ page }) => {
  await page.locator('#btabs button[data-t="hist"]').tap();
  await expect(page.locator('#b-hist')).toBeVisible();
  await expect(page.locator('#sheet-handle')).toBeVisible();
  await expect(page.locator('#btabs button[data-t="hist"]')).toHaveAttribute('aria-expanded', 'true');
  const sheet = (await page.locator('#bbody').boundingBox())!;
  const nav = (await page.locator('#btabs').boundingBox())!;
  expect(sheet.y + sheet.height).toBeLessThanOrEqual(nav.y + 2);
  expect(sheet.height).toBeGreaterThan(200);
  await page.locator('#btabs button[data-t="hist"]').tap();
  await expect(page.locator('#bbody')).toBeHidden();
});

test('the handle closes the sheet by tap and resizes it by dragging', async ({ page }) => {
  await page.locator('#btabs button[data-t="project"]').tap();
  const handle = page.locator('#sheet-handle');
  const before = (await page.locator('#bbody').boundingBox())!.height;
  const h = (await handle.boundingBox())!;
  await page.mouse.move(h.x + h.width / 2, h.y + 10);
  await page.mouse.down();
  await page.mouse.move(h.x + h.width / 2, h.y - 90, { steps: 6 });
  await page.mouse.up();
  expect((await page.locator('#bbody').boundingBox())!.height).toBeGreaterThan(before + 60);
  await handle.focus();
  await page.keyboard.press('Escape');
  await expect(page.locator('#bbody')).toBeHidden();
});

test('More lists Variants, Assistant and Log and opens the chosen panel; keyboard works', async ({ page }) => {
  const more = page.locator('#b-more');
  await more.tap();
  await expect(more).toHaveAttribute('aria-expanded', 'true');
  expect(await page.locator('#more-tabs [role=menuitem]').allTextContents()).toEqual(['Variants', 'Assistant', 'Log']);
  await page.locator('#more-tabs [role=menuitem]', { hasText: 'Log' }).tap();
  await expect(page.locator('#b-log')).toBeVisible();
  await expect(more).toHaveClass(/on/);
  await more.focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');
  await expect(page.locator('#more-tabs')).toBeHidden();
});
