import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// GUI flow 10: Ctrl+Shift+P is a command palette (Ctrl+K belongs to Koda) (commands and blocks), ? and F1 show the keys, the keys come from a registry that a person's own keys can replace.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

test('Ctrl+Shift+P opens the palette, a block can be found by name and chosen with Enter', async ({ page }) => {
  await page.keyboard.press('Control+Shift+P');
  const palette = page.locator('#cmdk');
  await expect(palette).toBeVisible();
  await page.locator('#cmdk-q').fill('drums');
  await expect(palette.locator('.cmdk-item[data-kind="block"]').first()).toContainText('drums');
  await page.keyboard.press('Enter');
  await expect(palette).toHaveCount(0);
  await expect(page.locator('#right')).toContainText('Drums');
});

test('? shows the sheet of keys with the views, and Esc closes it', async ({ page }) => {
  await page.keyboard.press('?');
  const sheet = page.locator('#keysheet');
  await expect(sheet).toBeVisible();
  await expect(sheet).toContainText('Ctrl+Shift+P');
  await expect(sheet).toContainText('F5');
  await expect(sheet).toContainText('Space');
  await page.keyboard.press('Escape');
  await expect(sheet).toHaveCount(0);
});

test('F1 shows the keys too, but ? typed in a field is only text', async ({ page }) => {
  await page.keyboard.press('F1');
  await expect(page.locator('#keysheet')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.locator('#lib-q').click();
  await page.keyboard.type('?');
  await expect(page.locator('#keysheet')).toHaveCount(0);
});

test('a person\'s own keys replace the defaults', async ({ page }) => {
  await page.evaluate(async () => { const m = await import('/app/keymap.js'); m.setKeys('help.keys', ['F2']); });
  await page.keyboard.press('F2');
  await expect(page.locator('#keysheet')).toBeVisible();
  await page.keyboard.press('Escape');
  await page.keyboard.press('?');
  await expect(page.locator('#keysheet')).toHaveCount(0);   // the old key no longer does it
  await page.evaluate(async () => { const m = await import('/app/keymap.js'); m.setKeys('help.keys', null); });
});

test('F6 without a sequencer block says so', async ({ page }) => {
  await page.keyboard.press('F6');
  await expect(page.locator('#toast')).toContainText('no sequencer block');
});
