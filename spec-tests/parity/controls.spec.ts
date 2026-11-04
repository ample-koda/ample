import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, node, openApp, state, openTab } from '../../web/tests/support';

freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); await node(page, 'master').locator('.nh .t').click(); await openTab(page, 'info'); });

const freeze = (page) => page.locator('[data-freeze="master"]');

test('the freeze ladder writes freeze and bake into the source and the explainer follows', async ({ page }) => {
  await expect(freeze(page)).toHaveValue('free');
  await freeze(page).selectOption('seeds');
  await expect.poll(async () => (await state(page)).text).toMatch(/master master freeze seeds \{/);
  await expect(page.locator('[data-controls="master"]')).toContainText('breaks reproduction');
  await freeze(page).selectOption('take');
  await expect.poll(async () => (await state(page)).text).toMatch(/master master freeze seeds bake \{|master master bake freeze seeds \{|master master bake \{/);
  await expect(page.locator('[data-controls="master"]')).toContainText('ignored');
  await freeze(page).selectOption('free');
  await expect.poll(async () => (await state(page)).text).toMatch(/master master \{/);
});

test('the melody level says what is kept', async ({ page }) => {
  await freeze(page).selectOption('plan');
  await expect.poll(async () => (await state(page)).text).toMatch(/freeze plan \{/);
  await expect(page.locator('[data-controls="master"]')).toContainText('melody codes and the seeds are stored');
  await page.locator('[data-controls="master"] button', { hasText: 'Unlock to edit' }).click();
  await expect(page.locator('#dlg-b')).toContainText('The melody is frozen');
  await dialog(page, 'Cancel');
});

test('regenerating warns first and an identical result changes nothing', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  const built = await page.evaluate(() => (window as any).AMPLE_STATE.build.takes.master);
  await page.locator('[data-controls="master"] button', { hasText: 'Regenerate' }).click();
  await expect(page.locator('#dlg-b')).toContainText('only guaranteed');
  await dialog(page, 'Regenerate');
  await expect(page.locator('#toast')).toContainText('Same result');
  expect((await api('/api/control/block?path=song.amplet&block=master')).selected).toBe(built);
});

test('Re-sing and Re-write open the variants view for the block', async ({ page }) => {
  await page.locator('[data-controls="master"] button', { hasText: 'Re-sing' }).click();
  await expect(page.locator('#b-var')).toBeVisible();
  await expect(page.locator('#v-vary')).toHaveValue('performance');
});

test('reproduction check compares a fresh native render and keeps the reference selected', async ({ page }) => {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await page.getByRole('button', { name: 'Check reproduction...', exact: true }).click();
  await expect(page.locator('#dlg')).toContainText('Reproduction: identical bytes');
  await expect(page.getByRole('button', { name: '▶ Reference take', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '▶ New render', exact: true })).toBeVisible();
});
