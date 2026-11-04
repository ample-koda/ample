import { test, expect } from '@playwright/test';
import { api, bottomTab, dialog, freshServer, node, openApp } from '../../web/tests/support';

freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

test('compare shows the changed files and the changed lines between two states', async ({ page }) => {
  await node(page, 'chords').locator('.nh .t').click();
  const input = page.locator('#props .prop', { hasText: 'bars' }).locator('input[type="number"]');
  await input.fill('20');
  await input.press('Tab');
  await page.click('#btn-save');
  await expect(page.locator('#b-dirty')).toBeHidden();
  const lyrics = await api('/api/project/new', { method: 'POST', body: { kind: 'lyrics', name: 'Compare' } });
  await api('/api/file', { method: 'PUT', body: { path: lyrics.path, text: '[ti:Compare]\n[00:00.00]new line\n' } });
  await page.click('#btn-cp');
  await dialog(page, 'Save all work and checkpoint', ['for compare']);
  await expect.poll(async () => (await api('/api/history/diff?from=studio&to=garage')).length).toBeGreaterThanOrEqual(2);
  await bottomTab(page, 'hist');
  await page.click('#h-compare');
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.locator('#dlg-extra select').first().selectOption('studio');
  await page.locator('#dlg-extra select').nth(1).selectOption('garage');
  await expect(page.locator('#dlg.compare-dialog')).toBeVisible();
  expect((await page.locator('#dlg').boundingBox())!.width).toBeGreaterThan(700);
  await page.locator('#dlg-extra select').evaluateAll((selects) => selects.forEach((select) => { (select as HTMLSelectElement).selectedOptions[0].textContent = 'long revision title '.repeat(30); }));
  const choicesFit = () => page.evaluate(() => {
    const extra = document.querySelector('#dlg-extra')!.getBoundingClientRect();
    const [a, b] = [...document.querySelectorAll('#dlg-extra select')].map((select) => select.getBoundingClientRect());
    return a.right <= extra.right + 1 && b.right <= extra.right + 1 && a.right < b.left;
  });
  expect(await choicesFit()).toBe(true);
  await page.setViewportSize({ width: 640, height: 700 });
  expect(await page.evaluate(() => {
    const extra = document.querySelector('#dlg-extra')!.getBoundingClientRect();
    const [a, b] = [...document.querySelectorAll('#dlg-extra select')].map((select) => select.getBoundingClientRect());
    return a.right <= extra.right + 1 && b.right <= extra.right + 1 && b.top > a.top;
  })).toBe(true);
  await page.setViewportSize({ width: 1200, height: 850 });
  await expect(page.locator('.compare-file')).toHaveCount((await api('/api/history/diff?from=studio&to=garage')).length);
  const song = page.locator('.compare-file[data-path="song.amplet"]');
  if (!(await song.evaluate((element) => (element as HTMLDetailsElement).open))) await song.locator('summary').click();
  await expect(song.locator('.compare-line.added').filter({ hasText: 'bars 20' }).first()).toBeVisible();
  await expect(song.locator('.compare-hunk')).toContainText('@@ -');
  const extra = page.locator('.compare-file').filter({ has: page.locator('.compare-path', { hasText: lyrics.path }) });
  if (!(await extra.evaluate((element) => (element as HTMLDetailsElement).open))) await extra.locator('summary').click();
  await expect(extra.locator('.compare-stats')).toHaveText('+2 −0');
  await expect(extra.locator('.compare-line.added')).toHaveCount(2);
  await expect(extra.locator('.compare-line.added').filter({ hasText: 'new line' }).first()).toBeVisible();
  await page.locator('#dlg-btns button', { hasText: 'Close' }).click();
});
