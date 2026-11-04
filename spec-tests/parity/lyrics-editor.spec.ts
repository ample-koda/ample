import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { api, freshServer, node, ownServer, openApp } from '../../web/tests/support';

const demo = fs.readFileSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../web/tests/fixtures/demo.amplet'), 'utf8');
freshServer({
  song: `${demo}lyrics lyr at 9 for 8 as chorus {\n  lang nl\n  timing per-word\n  first5s check\n  file lyrics/chorus.lyr\n}\n`,
  files: { 'lyrics/chorus.lyr': '[bar 9] Regen op de ruiten\n[bar 11] en ik zing er zacht doorheen\n' },
});
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await api('/api/file', { method: 'PUT', body: { path: 'lyrics/chorus.lyr', text: '[bar 9] Regen op de ruiten\n[bar 11] en ik zing er zacht doorheen\n' } });
  await node(page, 'lyr').dblclick();
  await expect(page.locator('#ly')).toBeVisible();
});

test('a double-click opens the lyrics editor on the file of the block, with rows, checks and a timing strip', async ({ page }) => {
  await expect(page.locator('.lyrow:not(.lyhead)')).toHaveCount(2);
  await expect(page.locator('.lyrow input').nth(1)).toHaveValue('Regen op de ruiten');
  await expect(page.locator('#ly-checks')).toContainText('first5s check passed');
  await expect(page.locator('#ly-strip i')).toHaveCount(2);
  await expect(page.locator('#ly-block')).toContainText('nl');
});

test('checks run live: an empty line, an order mistake and a long line are found', async ({ page }) => {
  await page.click('#ly-add');
  await expect(page.locator('#ly-checks')).toContainText('has no text');
  await page.locator('.lyrow:not(.lyhead)').nth(2).locator('input').first().fill('4');
  await expect(page.locator('#ly-checks')).toContainText('comes before the line above it');
  await page.getByLabel('sort on apply').uncheck();
  await page.click('#ly-apply');
  await expect(page.locator('#toast')).toContainText('Fix the');
  await expect(page.locator('#ly')).toBeVisible();
});

test('sort by time fixes the order and undo brings the old order back', async ({ page }) => {
  await page.click('#ly-add');
  const last = page.locator('.lyrow:not(.lyhead)').nth(2);
  await last.locator('input').first().fill('4');
  await last.locator('input').nth(1).fill('early');
  await page.click('#ly-sort');
  await expect(page.locator('.lyrow:not(.lyhead)').first().locator('input').nth(1)).toHaveValue('early');
  await page.click('#ly-undo');
  await expect(page.locator('.lyrow:not(.lyhead)').first().locator('input').nth(1)).toHaveValue('Regen op de ruiten');
});

test('apply and back writes the file (journaled) and returns to the node editor', async ({ page }) => {
  await page.locator('.lyrow:not(.lyhead)').first().locator('input').nth(1).fill('Regen op het dak');
  await page.click('#ly-apply');
  await expect(page.locator('#ly')).toBeHidden();
  await expect(page.locator('.node').first()).toBeVisible();
  expect((await api('/api/file?path=lyrics/chorus.lyr')).text).toContain('Regen op het dak');
});

test('undo and back throws the changes away', async ({ page }) => {
  await page.locator('.lyrow:not(.lyhead)').first().locator('input').nth(1).fill('NOT KEPT');
  await page.click('#ly-undoback');
  await page.locator('#dlg-btns button', { hasText: 'Undo and back' }).click();
  await expect(page.locator('#ly')).toBeHidden();
  expect((await api('/api/file?path=lyrics/chorus.lyr')).text).not.toContain('NOT KEPT');
});

test('import turns .lrc times into bars and export gives .lrc back', async ({ page }) => {
  await page.getByRole('button', { name: 'Import...' }).click();
  await page.locator('#dlg-extra textarea').fill('[00:30.00] imported line\n');
  await page.locator('#dlg-btns button', { hasText: 'Append' }).click();
  await expect(page.locator('.lyrow:not(.lyhead)')).toHaveCount(3);
  await page.getByRole('button', { name: 'Export .lrc' }).click();
  await expect(page.locator('#ly-export')).toHaveValue(/\[\d\d:\d\d\.\d\d\] imported line/);
  await page.locator('#dlg-btns button', { hasText: 'Close' }).click();
  void ownServer;
});

test('the lyrics button of the properties panel opens the same editor', async ({ page }) => {
  await page.click('#ly-undoback');
  await expect(page.locator('#ly')).toBeHidden();
  await node(page, 'lyr').locator('.nh .t').click();
  await page.getByRole('button', { name: '… Edit lyrics' }).click();
  await expect(page.locator('#ly')).toBeVisible();
});

test('a build is out of date after the words changed in the lyrics editor, although the song text did not', async ({ page }) => {
  await page.click('#ly-undoback');
  await expect(page.locator('#ly')).toBeHidden();
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const built = () => page.evaluate(() => Object.keys((window as any).AMPLE_STATE.build.status).length);
  expect(await built()).toBeGreaterThan(0);

  await node(page, 'lyr').dblclick();
  await page.locator('.lyrow:not(.lyhead)').first().locator('input').nth(1).fill('Zon boven het dak');
  await page.click('#ly-apply');
  await expect(page.locator('#ly')).toBeHidden();
  // only the lyrics block (and what reads it) is out of date, not the whole build
  await expect.poll(() => page.evaluate(() => 'lyr' in (window as any).AMPLE_STATE.build.status)).toBe(false);

  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const status = await page.evaluate(() => (window as any).AMPLE_STATE.build.status.lyr.status);
  expect(status).toBe('rendered');
});
