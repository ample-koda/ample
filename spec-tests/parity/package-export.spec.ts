import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

// The .ampletx export, viewer and import dialogs. The demo song is saved, so a Full package can be made and read back.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

const openExport = async (page) => {
  await page.locator('.savemore').first().click();
  await page.locator('#menu div', { hasText: 'Export package' }).first().click();
  await expect(page.locator('#tx-export')).toBeVisible();
};

test('the export dialog shows what goes in and writes a Full package that is never overwritten', async ({ page }) => {
  await openExport(page);
  await expect(page.locator('#tx-preview .tx-tiles')).toBeVisible({ timeout: 90000 });
  await expect(page.locator('#tx-do-export')).toBeEnabled();
  await page.locator('#tx-name').fill('spec-full');
  await page.click('#tx-do-export');
  await expect(page.locator('#tx-done')).toBeVisible({ timeout: 90000 });
  const file = path.join(ownServer()!.folder, 'exports', 'spec-full.ampletx');
  expect(fs.statSync(file).size).toBeGreaterThan(1000);
  expect(fs.readFileSync(file, 'utf8').startsWith('#AMPLETX 1')).toBe(true);
  // the same name again is refused, nothing is overwritten
  await page.click('#tx-do-export');
  await expect(page.locator('#tx-result .tx-error')).toContainText('AMP5435');
});

test('the FLAC option only exists for Derived and asks for a listening test before it writes', async ({ page }) => {
  await openExport(page);
  await expect(page.locator('#tx-opt-flac')).toBeHidden();
  await page.locator('input[name="tx-mode"][value="derived"]').check();
  await expect(page.locator('#tx-opt-flac')).toBeVisible();
  await expect(page.locator('#tx-export small', { hasText: 'NO guarantee' })).toBeVisible();
  await page.locator('input[name="tx-mode"][value="full"]').check();
  await expect(page.locator('#tx-opt-flac')).toBeHidden();
});

test('the viewer opens a package read-only and the import plan lists the files before anything is written', async ({ page }) => {
  await openExport(page);
  await expect(page.locator('#tx-preview .tx-tiles')).toBeVisible({ timeout: 90000 });
  await page.locator('#tx-name').fill('spec-view');
  await page.click('#tx-do-export');
  await expect(page.locator('#tx-done')).toBeVisible({ timeout: 90000 });
  await page.getByRole('button', { name: 'Open it read-only' }).click();
  await expect(page.locator('#tx-viewer')).toBeVisible();
  await expect(page.locator('#tx-import')).toBeVisible();
  await page.click('#tx-import');
  await expect(page.locator('#tx-plan')).toBeVisible({ timeout: 60000 });
  await expect(page.locator('#tx-plan')).not.toContainText('Comparing the package');
});
