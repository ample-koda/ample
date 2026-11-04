import { test, expect } from '@playwright/test';
import { freshServerPerTest, node, openApp } from '../../web/tests/support';

freshServerPerTest();
test.use({ permissions: ['microphone'], launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] } });
test.beforeEach(async ({ page }) => { await openApp(page); });

test('a recording stays in the browser until "Use in project" uploads the chosen take as an import-audio block', async ({ page }) => {
  const uploads: string[] = [];
  page.on('request', (request) => { if (request.url().includes('/api/project/upload')) uploads.push(request.url()); });
  await page.getByRole('button', { name: 'Record', exact: true }).click();
  const dialog = page.locator('dialog.recorder');
  await expect(dialog).toBeVisible();
  await dialog.locator('#rec-count').uncheck();
  await dialog.getByRole('button', { name: /Record$/ }).click();
  await expect(dialog.getByRole('button', { name: /Stop/ })).toBeEnabled({ timeout: 15000 });   // under load the recorder takes a while to start
  await page.waitForTimeout(1200);
  await dialog.getByRole('button', { name: /Stop/ }).click();
  await expect(dialog.locator('.rec-take')).toHaveCount(1, { timeout: 15000 });
  await expect(dialog.getByRole('button', { name: 'Use in project' })).toBeEnabled();
  expect(uploads).toHaveLength(0);
  await dialog.getByRole('button', { name: 'Use in project' }).click();
  await expect(node(page, 'recording')).toBeVisible({ timeout: 15000 });
  await expect.poll(() => uploads.length, { timeout: 10000 }).toBe(1);
});

test('with a count-in the take starts at the downbeat: the four clicks are cut away, so a second of singing is a take of about a second', async ({ page }) => {
  await page.getByRole('button', { name: 'Record', exact: true }).click();
  const dialog = page.locator('dialog.recorder');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: /Record$/ }).click();
  await expect(dialog.getByText('Recording...')).toBeVisible({ timeout: 15000 });
  await page.waitForTimeout(1500);
  await dialog.getByRole('button', { name: /Stop/ }).click();
  const take = dialog.locator('.rec-take');
  await expect(take).toHaveCount(1);
  const seconds = Number(/· ([\d.]+) s/.exec(await take.innerText())![1]);
  expect(seconds).toBeGreaterThan(1.2);
  expect(seconds).toBeLessThan(2.2);     // with the count-in (about two seconds at 120 bpm) in it the take would be over three seconds
});

test('stopping during the count-in leaves no take', async ({ page }) => {
  await page.getByRole('button', { name: 'Record', exact: true }).click();
  const dialog = page.locator('dialog.recorder');
  await expect(dialog.getByLabel('Input device').locator('option').first()).toBeAttached();
  await dialog.getByRole('button', { name: /Record$/ }).click();
  await expect(dialog.getByText('Count-in...')).toBeVisible({ timeout: 15000 });
  await dialog.getByRole('button', { name: /Stop/ }).click();
  await page.waitForTimeout(300);
  await expect(dialog.locator('.rec-take')).toHaveCount(0);
});
