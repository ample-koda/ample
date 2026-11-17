import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// GUI flow 4, strips: the real names and colours of the sources, empty inputs dim, a pan knob, a fader that resets, a clear-solo control, the master next to the output.
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  await expect(page.locator('#b-mixer .mixview')).toBeVisible();
});
const param = async (block: string, name: string) => String((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === name)?.value ?? '').replace(/"/g, '');

test('every input shows the name and colour of its source block, an empty input is dim', async ({ page }) => {
  const lanes = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan');
  await expect(lanes.nth(0).locator('.mixsrc')).toHaveText('drums');
  await expect(lanes.nth(1).locator('.mixsrc')).toHaveText('bass');
  await expect(lanes.nth(2).locator('.mixsrc')).toHaveText('piano');
  await expect(lanes.nth(0).locator('.mixicon')).not.toHaveText('');
  await expect(lanes).toHaveCount(3);
  await page.getByRole('button', { name: 'mix show 1 empty inputs' }).click();
  await expect(lanes.nth(3)).toHaveClass(/empty/);
  await expect(lanes.nth(3).locator('.mixsrc')).toHaveText('—');
  const colours = await lanes.evaluateAll((l) => l.map((x) => (x as HTMLElement).style.getPropertyValue('--mc')));
  expect(new Set(colours.slice(0, 3)).size).toBe(3);
  await expect(page.locator('#b-mixer .mixsrc', { hasText: /^[0-9]$/ })).toHaveCount(0);
});

test('the pan is a knob: keys move it, double-click centres it, and it is written into the song', async ({ page }) => {
  const knob = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan').nth(1).locator('.mixpan svg');
  await knob.focus();
  for (let i = 0; i < 3; i++) await page.keyboard.press('ArrowRight');
  await expect.poll(() => param('mix', 'pan')).toMatch(/^c [0-9]+ c c$/);
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan').nth(1).locator('.mixpan svg').dblclick();
  await expect.poll(() => param('mix', 'pan')).toBe('c c c c');
});

test('double-click on a fader gives 0 dB back; the dB scale and the peak hold are there', async ({ page }) => {
  const fader = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixfader').first();
  await fader.evaluate((i: HTMLInputElement) => { i.value = '-9'; i.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(async () => (await param('mix', 'gains')).split(' ')[0]).toBe('-9');
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixfader').first().dblclick();
  await expect.poll(async () => (await param('mix', 'gains')).split(' ')[0]).toBe('0');
  await expect(page.locator('#b-mixer .mixstrip[data-block="mix"] .mixticks').first()).toContainText('-12');
  await expect(page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchm').first().locator('.mixpk')).toHaveCount(2);
});

test('the fader is as tall as the panel allows, and Clear solo writes every solo off', async ({ page }) => {
  const box = await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixfader').first().boundingBox();
  expect(box!.height).toBeGreaterThan(55);
  const clear = page.getByRole('button', { name: 'mix clear solos' });
  await expect(clear).toBeDisabled();
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixsolo').first().click();
  await expect(clear).toBeEnabled();
  await clear.click();
  await expect.poll(() => param('mix', 'solos')).toBe('off off off off');
  await expect(clear).toBeDisabled();
});

test('the master strip is the last strip, right next to the output meter', async ({ page }) => {
  const ids = await page.locator('#b-mixer .mixview > .mixstrip').evaluateAll((l) => l.map((x) => (x as HTMLElement).dataset.block || 'output'));
  expect(ids.slice(-2)).toEqual(['master', 'output']);
});
