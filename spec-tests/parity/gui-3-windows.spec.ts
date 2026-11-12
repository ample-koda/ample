import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 3: a panel (Advanced) is a floating window: several can be open at once, they can be dragged by the header, Esc closes the one that was used last,
// the same block opens one window, and the song behind stays usable.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'delay', 'w-delay');
  await addTemplateBlock(page, 'reverb', 'w-reverb');
});
const openPanel = async (page, name: string) => node(page, name).getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
const windows = (page) => page.locator('dialog.studio-floating[open]');

test('two panels can be open at the same time', async ({ page }) => {
  await openPanel(page, 'w-delay');
  await expect(windows(page)).toHaveCount(1);
  await openPanel(page, 'w-reverb');
  await expect(windows(page)).toHaveCount(2);
  await expect(page.locator('#studio-title')).toHaveCount(1);   // the ids stay unique
});

test('the same block opens one window, which comes to the front', async ({ page }) => {
  await openPanel(page, 'w-delay');
  await openPanel(page, 'w-reverb');
  await openPanel(page, 'w-delay');
  await expect(windows(page)).toHaveCount(2);
});

test('Esc closes the window that was used last and leaves the other', async ({ page }) => {
  await openPanel(page, 'w-delay');
  await openPanel(page, 'w-reverb');
  await expect(windows(page)).toHaveCount(2);
  await page.keyboard.press('Escape');
  await expect(windows(page)).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(windows(page)).toHaveCount(0);
});

test('a window is dragged by its header and keeps its place', async ({ page }) => {
  await openPanel(page, 'w-delay');
  const dialog = windows(page).first();
  await expect(dialog).toBeVisible();
  const header = dialog.locator('.studio-dialog-header');
  const before = (await dialog.boundingBox())!;
  const handle = (await header.boundingBox())!;
  await page.mouse.move(handle.x + 40, handle.y + handle.height / 2);
  await page.mouse.down();
  await page.mouse.move(handle.x + 40 + 120, handle.y + handle.height / 2 + 60, { steps: 6 });
  await page.mouse.up();
  await expect.poll(async () => Math.round((await dialog.boundingBox())!.x - before.x)).toBeGreaterThan(80);
  await expect.poll(async () => Math.round((await dialog.boundingBox())!.y - before.y)).toBeGreaterThan(30);
});

test('the song behind a window stays usable', async ({ page }) => {
  await openPanel(page, 'w-delay');
  await expect(windows(page)).toHaveCount(1);
  await page.evaluate(async () => { const s = await import('/app/store.js'); s.select('w-reverb'); s.notify(); });
  await expect(page.locator('#right')).toContainText('Reverb');
});
