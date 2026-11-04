import { test, expect } from '@playwright/test';
import { freshServer, openApp, srcText } from '../../web/tests/support';

// The Check button of the source editor: a short note when nothing is found, otherwise a window with every finding and a click goes to its line.
freshServer({ song: 'composition "c" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords {\n  chords C F\n  bars 2\n}\n' });
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#vs-source'); await expect.poll(() => srcText(page)).toMatch(/composition/); });

test('Check lists what the language server found and a click goes to the line', async ({ page }) => {
  await page.locator('#se-check').click();
  const dialog = page.locator('#dlg');
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('.check-row').first()).toBeVisible();
  expect(await dialog.locator('.check-row').count()).toBeGreaterThan(0);
  const pos = (await dialog.locator('.check-row .check-pos').first().textContent())!;
  await dialog.locator('.check-row').first().click();
  await expect(dialog).toBeHidden();
  expect(pos).toMatch(/^\d+:\d+$/);
});
