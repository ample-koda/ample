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

const flagsOf = async (name: string) => {
  const doc = await api('/api/doc?path=song.amplet');
  return String(doc.blocks.find((b: { name: string }) => b.name === 'mix')?.parameters.find((p: { name: string }) => p.name === name)?.value ?? '').replace(/"/g, '').trim();
};

test('mute writes mutes on for that input, keeps the gain, and a second click writes off again; Undo is one step', async ({ page }) => {
  const gains = (await gainsOf()).join(' ');
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixmute').first().click();
  await expect.poll(() => flagsOf('mutes')).toBe('on off off off');
  expect((await gainsOf()).join(' ')).toBe(gains);
  await expect(page.locator('#b-mixer .mixstrip[data-block="mix"] .mixmute').first()).toHaveClass(/on/);
  await page.locator('#btn-undo').click();
  await expect.poll(() => flagsOf('mutes')).not.toContain('on');
  await page.locator('#btn-redo').click();
  await expect.poll(() => flagsOf('mutes')).toBe('on off off off');
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixmute').first().click();
  await expect.poll(() => flagsOf('mutes')).toBe('off off off off');
});

test('solo writes solos on for that input only; the other inputs are drawn silent and keep their gain', async ({ page }) => {
  const gains = (await gainsOf()).join(' ');
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixsolo').nth(1).click();
  await expect.poll(() => flagsOf('solos')).toBe('off on off off');
  expect((await gainsOf()).join(' ')).toBe(gains);
  const lanes = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan');
  await expect(lanes.nth(0)).toHaveClass(/muted/);
  await expect(lanes.nth(1)).not.toHaveClass(/muted/);
  await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixsolo').nth(1).click();
  await expect.poll(() => flagsOf('solos')).toBe('off off off off');
});

test('every shown input has a small meter; the empty input at the end is behind a toggle', async ({ page }) => {
  const meters = page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchm');
  await expect(meters).toHaveCount(3);
  const toggle = page.getByRole('button', { name: 'mix show 1 empty inputs' });
  await expect(toggle).toHaveText('+ 1 empty input');
  await toggle.click();
  await expect(meters).toHaveCount(4);
  await page.getByRole('button', { name: 'mix hide 1 empty inputs' }).click();
  await expect(meters).toHaveCount(3);
});

test('the mixer picker is a compact control above the row, not a column', async ({ page }) => {
  await expect(page.locator('#b-mixer .mixscope')).toBeVisible();   // boundingBox() does not wait for the row to be drawn
  await expect(page.locator('#b-mixer .mixstrip').first()).toBeVisible();
  const picker = await page.locator('#b-mixer .mixscope').boundingBox();
  const first = await page.locator('#b-mixer .mixstrip').first().boundingBox();
  expect(picker!.height).toBeLessThan(40);
  expect(picker!.y + picker!.height).toBeLessThanOrEqual(first!.y);
});

test('the bottom panel is tall enough for a whole strip by default', async ({ page }) => {
  const strip = await page.locator('#b-mixer .mixstrip[data-block="mix"]').boundingBox();
  const view = await page.locator('#b-mixer .mixview').boundingBox();
  expect(strip!.y + strip!.height).toBeLessThanOrEqual(view!.y + view!.height + 1);
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

test('the master strip has its own fader and meter, and the fader writes the gain of the master', async ({ page }) => {
  const strip = page.locator('#b-mixer .mixstrip[data-block="master"]');
  await expect(strip.locator('.mixstripm')).toHaveCount(1);
  await strip.locator('.mixstripfader').evaluate((input: HTMLInputElement) => { input.value = '-3'; input.dispatchEvent(new Event('change', { bubbles: true })); });   // a master has no gain: the fader is the ceiling of its limiter
  await expect.poll(async () => {
    const doc = await api('/api/doc?path=song.amplet');
    return String(doc.blocks.find((b: { name: string }) => b.name === 'master')?.parameters.find((p: { name: string }) => p.name === 'limiter')?.value ?? '').replace(/"/g, '');
  }).toBe('-3 dB');
});

test('the per-input meters are wide enough to see', async ({ page }) => {
  const box = await page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchm').first().boundingBox();
  expect(box!.width).toBeGreaterThanOrEqual(20);
});

test('the master fader value reaches the render (a different limiter ceiling gives a different take)', async ({ page }) => {
  const strip = page.locator('#b-mixer .mixstrip[data-block="master"]');
  await strip.locator('.mixstripfader').evaluate((input: HTMLInputElement) => { input.value = '-12'; input.dispatchEvent(new Event('change', { bubbles: true })); });
  await expect.poll(async () => String((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'master')?.parameters.find((p: { name: string }) => p.name === 'limiter')?.value ?? '')).toContain('-12');
  const quiet = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 2 } } });
  const loud = await api('/api/build', { method: 'POST', body: { path: 'song.amplet', intent: 'preview', range: { firstBar: 1, bars: 2 }, override: { block: 'master', name: 'limiter', value: '"0 dB"' } } });
  expect(quiet.masterTakeId).toBeTruthy();
  expect(loud.masterTakeId).toBeTruthy();
  expect(loud.masterTakeId).not.toBe(quiet.masterTakeId);
});
