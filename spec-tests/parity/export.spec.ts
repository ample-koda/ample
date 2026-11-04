import * as fs from 'node:fs';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

freshServer();

const backupPage = async (page) => {
  await openApp(page, { reset: false });
  await page.click('#btn-projects');
  await page.click('#pm [data-page="backup"]');
};

test('an export writes one archive file', async ({ page }) => {
  await backupPage(page);
  const file = `${ownServer()!.folder}-export.ample`;
  await page.locator('#ex-file').fill(file);
  await page.click('#ex-go');
  await expect(page.locator('#toast')).toContainText('Exported');
  expect(fs.statSync(file).size).toBeGreaterThan(100);
  fs.rmSync(file, { force: true });
});

test('without a file name it asks for one', async ({ page }) => {
  await backupPage(page);
  await page.click('#ex-go');
  await expect(page.locator('#toast')).toContainText('Choose a file');
});
