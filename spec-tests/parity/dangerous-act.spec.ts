import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, openApp } from '../../web/tests/support';

freshServer();

test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); await page.click('#btn-projects'); await page.click('#pm [data-page="settings"]'); });

test('the dialog needs the typed words and a reason, and Cancel changes nothing', async ({ page }) => {
  const before = (await api('/api/project/settings')).masterSeed;
  await page.click('#ps-tabbtn-seed');
  await page.locator('#ps-newseed').fill('777');
  await page.click('#ps-changeseed');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#dlg-b')).toContainText('change master seed');
  await dialog(page, 'Do it', ['wrong words', 'because']);
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await dialog(page, 'Cancel');
  expect((await api('/api/project/settings')).masterSeed).toBe(before);
});

test('the second call cannot be made without the dry run, the words and a reason', async () => {
  const bogus = await api('/api/acts/confirm', { method: 'POST', body: { token: 'bogus', typed: 'change master seed', reason: 'x' } });
  expect(JSON.stringify(bogus)).toMatch(/AMP/);
  const dry = await api('/api/acts/master-seed', { method: 'POST', body: { seed: '5' } });
  const wrong = await api('/api/acts/confirm', { method: 'POST', body: { token: dry.token, typed: 'nope', reason: 'x' } });
  expect(JSON.stringify(wrong)).toMatch(/AMP/);
  const empty = await api('/api/acts/confirm', { method: 'POST', body: { token: dry.token, typed: dry.acceptance, reason: '' } });
  expect(JSON.stringify(empty)).toMatch(/AMP/);
});

test('with the words and a reason the act is done', async ({ page }) => {
  await page.click('#ps-tabbtn-seed');
  await page.locator('#ps-newseed').fill('31337');
  await page.click('#ps-changeseed');
  await dialog(page, 'Do it', ['change master seed', 'testing the dialog']);
  await expect.poll(async () => (await api('/api/project/settings')).masterSeed).toBe('31337');
});
