import { test, expect, type Page } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// An existing project (its project.json has no first-run field) gets the wizard once too; Finish goes back to the song that was open and plays nothing.
freshServer({
  firstRun: true,
  prepare: (_cli, folder) => {
    const file = path.join(folder, '.ample', 'project.json');
    const json = JSON.parse(fs.readFileSync(file, 'utf8'));
    delete json.settings.firstRun;
    fs.writeFileSync(file, JSON.stringify(json, null, 2) + '\n');
  },
});
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const openPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);

test('the wizard of an existing project says so, and Finish leaves the open song alone and does not play', async ({ page }) => {
  await expect(page.locator('#fr')).toBeVisible();
  await expect(page.locator('#fr-body')).toContainText('made before the first-time setup existed');
  const before = await openPath(page);
  for (let i = 0; i < 7; i++) await page.locator('#fr-next').click();
  await expect(page.locator('#fr-body')).toContainText('takes you back to your song');
  await page.locator('#fr-next').click();
  await expect(page.locator('#fr')).toHaveCount(0);
  expect(await openPath(page)).toBe(before);
  await expect(page.locator('#btxt')).toHaveText('idle');   // nothing builds or plays by itself
  const state = await page.evaluate(async () => (await fetch('/api/project/first-run', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' } })).json());
  expect(state.pending).toBe(false);
  expect(state.origin).toBe('existing');
});
