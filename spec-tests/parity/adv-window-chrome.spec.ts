import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// The Advanced windows have the window controls of AMPLE Jam: collapse, maximize (Esc restores), a resize grip with a remembered size, and the metal frame.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'c-comp');
  await addTemplateBlock(page, 'reverb', 'c-rev');
});
const openPanel = async (page, name: string) => node(page, name).getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
const win = (page) => page.locator('dialog.studio-floating[open]').first();

test('the window has the metal frame, collapse, maximize and a resize grip', async ({ page }) => {
  await openPanel(page, 'c-comp');
  const w = win(page);
  await expect(w).toHaveClass(/metal-window/);
  await expect(w.locator('.mls-collapse')).toBeVisible();
  await expect(w.locator('.mls-maximize')).toBeVisible();
  await expect(w.locator('.mls-grip')).toBeVisible();
});

test('collapse and double-click roll the window up to its title bar, which stays draggable', async ({ page }) => {
  await openPanel(page, 'c-comp');
  const w = win(page);
  const full = (await w.boundingBox())!;
  await w.locator('.mls-collapse').click();
  await expect(w).toHaveAttribute('data-window', 'collapsed');
  await expect(w.locator('.studio-dialog-body')).toBeHidden();
  const small = (await w.boundingBox())!;
  expect(small.height).toBeLessThan(80);
  const header = (await w.locator('.studio-dialog-header').boundingBox())!;
  await page.mouse.move(header.x + 20, header.y + header.height / 2);
  await page.mouse.down(); await page.mouse.move(header.x + 120, header.y + 90, { steps: 5 }); await page.mouse.up();
  await expect.poll(async () => Math.round((await w.boundingBox())!.y - small.y)).toBeGreaterThan(40);
  await w.locator('.studio-dialog-header strong').dblclick();
  await expect(w).not.toHaveAttribute('data-window', /./);
  expect((await w.boundingBox())!.height).toBeGreaterThan(full.height - 5);
});

test('maximize fills the screen, Esc restores it first and a second Esc closes it', async ({ page }) => {
  await openPanel(page, 'c-comp');
  const w = win(page);
  await w.locator('.mls-maximize').click();
  await expect(w).toHaveAttribute('data-window', 'max');
  expect((await w.boundingBox())!.width).toBeGreaterThan(1400);
  await page.keyboard.press('Escape');
  await expect(w).not.toHaveAttribute('data-window', /./);
  await expect(w).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('dialog.studio-floating[open]')).toHaveCount(0);
});

test('the size is remembered per panel type', async ({ page }) => {
  await openPanel(page, 'c-comp');
  let w = win(page);
  const before = (await w.boundingBox())!;
  const grip = (await w.locator('.mls-grip').boundingBox())!;
  await page.mouse.move(grip.x + 10, grip.y + 10);
  await page.mouse.down(); await page.mouse.move(grip.x + 90, grip.y + 50, { steps: 5 }); await page.mouse.up();
  await expect.poll(async () => Math.round((await w.boundingBox())!.width - before.width)).toBeGreaterThan(50);
  const sized = (await w.boundingBox())!;
  await page.keyboard.press('Escape');
  await expect(page.locator('dialog.studio-floating[open]')).toHaveCount(0);
  await openPanel(page, 'c-comp');
  w = win(page);
  await expect.poll(async () => Math.round((await w.boundingBox())!.width)).toBe(Math.round(sized.width));
  await expect.poll(async () => Math.round((await w.boundingBox())!.height)).toBe(Math.round(sized.height));
});
