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

const gainsOf = async () => {
  const doc = await api('/api/doc?path=song.amplet');
  return String(doc.blocks.find((b: { name: string }) => b.name === 'mix')?.parameters.find((p: { name: string }) => p.name === 'gains')?.value ?? '').replace(/"/g, '').trim().split(/\s+/);
};

test('mute puts an input at -60 dB and a second click puts the level back', async ({ page }) => {
  const before = (await gainsOf())[0];
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixmute').first().click();
  await expect.poll(async () => (await gainsOf())[0]).toBe('-60');
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixmute').first().click();
  await expect.poll(async () => (await gainsOf())[0]).toBe(before);
});

test('solo mutes the other inputs and a second click brings them back', async ({ page }) => {
  const before = await gainsOf();
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixsolo').nth(1).click();
  await expect.poll(async () => (await gainsOf()).join(' ')).toBe([before[0] ? '-60' : '-60', before[1], '-60', '-60'].join(' '));
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixsolo').nth(1).click();
  await expect.poll(async () => (await gainsOf()).join(' ')).toBe(before.join(' '));
});

test('every input has a small meter', async ({ page }) => {
  await expect(page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchm')).toHaveCount(4);
});

test('choosing one mixer in the list shows only that mixer, and All mixers shows them all again', async ({ page }) => {
  const picker = page.locator('#b-mixer .mixscope');
  await expect(picker).toBeVisible();
  const all = await page.locator('#b-mixer .mixstrip').count();
  await picker.selectOption({ index: 1 });
  await expect(page.locator('#b-mixer .mixstrip:not(.mixmeter)')).toHaveCount(1);
  await picker.selectOption('');
  await expect(page.locator('#b-mixer .mixstrip')).toHaveCount(all);
});
