import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// The reusable window (app/window.js): the Jam look, dragged by its title, modal on request, and a close that asks about unsaved changes.
test.beforeEach(async ({ page }) => { await openApp(page); });

const open = (page, options: string) => page.evaluate(async (src) => {
  const { openWindow } = await import('/app/window.js');
  const options = new Function('el', `return (${src})`)((tag: string, text: string) => Object.assign(document.createElement(tag), { textContent: text }));
  (window as any).__w = openWindow(options);
  (window as any).__w.closed.then((reason: string) => { (window as any).__reason = reason; });
}, options);

test('a window is dragged by its title and stays on screen', async ({ page }) => {
  await open(page, "{ id: 't1', title: 'Hello', content: el('p', 'body'), width: 420, height: 260 }");
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  const before = await win.boundingBox();
  const bar = await page.locator('.aw-bar').boundingBox();
  await page.mouse.move(bar!.x + 40, bar!.y + 10); await page.mouse.down(); await page.mouse.move(bar!.x + 140, bar!.y + 90, { steps: 6 }); await page.mouse.up();
  const after = await win.boundingBox();
  expect(Math.round(after!.x - before!.x)).toBe(100);
  expect(Math.round(after!.y - before!.y)).toBe(80);
  await page.locator('.aw-close').click();
  await expect(win).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__reason)).toBe('closed');
});

test('a modal window closes at once without changes, and asks Save / Don\'t save / Cancel with changes', async ({ page }) => {
  await open(page, "{ title: 'Beat', modal: true, content: el('p', 'x'), dirty: () => window.__dirty === true, save: async () => { window.__saved = true; return true; } }");
  const win = page.locator('dialog.ample-win');
  await expect(win).toBeVisible();
  // a click beside it (on the backdrop) closes it when nothing changed
  await page.mouse.click(5, 5);
  await expect(win).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__reason)).toBe('closed');
  // with changes: Escape asks, Cancel keeps it, Don't save closes
  await page.evaluate(() => { (window as any).__dirty = true; });
  await open(page, "{ title: 'Beat', modal: true, content: el('p', 'x'), dirty: () => window.__dirty === true, save: async () => { window.__saved = true; return true; } }");
  await page.keyboard.press('Escape');
  await expect(page.getByRole('alertdialog')).toBeVisible();
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.getByRole('alertdialog')).toHaveCount(0);
  await expect(win).toBeVisible();
  await page.mouse.click(5, 5);
  await expect(page.getByRole('alertdialog')).toBeVisible();
  await page.getByRole('button', { name: "Don't save" }).click();
  await expect(win).toHaveCount(0);
  expect(await page.evaluate(() => (window as any).__reason)).toBe('discarded');
  // Save calls the save function and closes
  await open(page, "{ title: 'Beat', modal: true, content: el('p', 'x'), dirty: true, save: async () => { window.__saved = true; return true; } }");
  await page.locator('.aw-close').click();
  await page.getByRole('alertdialog').getByRole('button', { name: 'Save', exact: true }).click();
  await expect(win).toHaveCount(0);
  expect(await page.evaluate(() => [(window as any).__saved, (window as any).__reason])).toEqual([true, 'saved']);
});
