import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer({ prepare: (cli, folder) => cli('blackbox', 'simulate-crash', 'song.amplet', '--project', folder) });

const health = async (page) => {
  await openApp(page, { reset: false });
  await page.click('#btn-projects');
  await page.click('#pm [data-page="health"]');
};

test('an interrupted command is found and can be accepted', async ({ page }) => {
  await health(page);
  await expect(page.locator('#pm')).toContainText('AMPLE Redux');
  await expect(page.locator('#hl-blackbox')).toContainText('hash chain intact');
  await expect(page.locator('[data-seq]').first()).toContainText('file.write');
  await page.locator('[data-seq] button', { hasText: 'Accept the file as it is' }).first().click();
  await expect(page.locator('#hl-interrupted')).toContainText('Nothing was interrupted');
});

test('the backup age is reported', async ({ page }) => {
  await health(page);
  await expect(page.locator('#hl-backup')).toContainText('no backup');
});

test('AMPLE Redux compaction is an explicit preview action', async ({ page }) => {
  await health(page);
  await page.click('#hl-redux-compact');
  await expect(page.locator('#toast')).toContainText('nothing to compact');
});
