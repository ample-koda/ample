import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// GUI flow 4: the Mixer tab of the bottom panel has a strip for every mixer and master block, faders write into the song, and the output has a meter.
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  await expect(page.locator('#b-mixer')).toBeVisible();
});

test('a strip for every mixer and master block of the song, and a meter for the output', async ({ page }) => {
  await expect(page.locator('#b-mixer .mixstrip[data-block="mix"]')).toBeVisible();
  await expect(page.locator('#b-mixer .mixstrip[data-block="master"]')).toBeVisible();
  await expect(page.locator('#b-mixer .mixmeter .mixbars')).toBeVisible();
});

test('a fader writes its gain into the song', async ({ page }) => {
  const fader = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixfader').first();
  await fader.evaluate((input: HTMLInputElement) => { input.value = '-6'; input.dispatchEvent(new Event('input', { bubbles: true })); input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(async () => {
    const doc = await api('/api/doc?path=song.amplet');
    return doc.blocks.find((b: { name: string }) => b.name === 'mix')?.parameters.find((p: { name: string }) => p.name === 'gains')?.value ?? '';
  }).toMatch(/^"?-6 /);
});

test('the strip of the master has a button for its advanced controls', async ({ page }) => {
  await page.locator('#b-mixer .mixstrip[data-block="master"] button', { hasText: 'Controls' }).click();
  await expect(page.locator('dialog.studio-floating[open]')).toBeVisible();
});
