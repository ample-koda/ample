import { test, expect, type Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// GUI flow 4, sends: a send knob on an input puts a send block, a shared bus and a reverb or delay into the song (blocks the language has); the second input reuses the bus; Undo takes it back in one step.
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  await expect(page.locator('#b-mixer .mixview')).toBeVisible();
});
type B = { name: string; parameters: { name: string; value: string }[] };
const blocks = async () => (await api('/api/doc?path=song.amplet')).blocks as B[];
const param = (b: B[], name: string, p: string) => String(b.find((x) => x.name === name)?.parameters.find((x) => x.name === p)?.value ?? '').replace(/"/g, '');
const lane = (page: Page, n: number) => page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan').nth(n);

test('a reverb send is written as send block, bus and reverb; the second input shares the bus; Undo removes it in one step', async ({ page }) => {
  const knob = (n: number) => lane(page, n).locator('.mixsend svg').first();
  await knob(1).focus();
  for (let i = 0; i < 12; i++) await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await blocks()).some((b) => b.name === 'bass-send-reverb')).toBe(true);
  const b1 = await blocks();
  for (const n of ['reverb-bus', 'reverb-return']) expect(b1.some((b) => b.name === n)).toBe(true);
  expect(param(b1, 'reverb-return', 'mix')).toBe('100%');
  // the dry sound still reaches the mixer through the send block; the return is a lane of the mixer with the name of the bus
  await expect(lane(page, 1).locator('.mixsrc')).toHaveText('bass');
  await expect(lane(page, 3).locator('.mixsrc')).toHaveText('reverb-bus');
  await expect(lane(page, 1).locator('.mixsend svg').first()).toHaveAttribute('aria-valuenow', /^-[0-9]+$/);
  // a second input uses the same bus
  await knob(2).focus();
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await blocks()).some((b) => b.name === 'piano-send-reverb')).toBe(true);
  expect((await blocks()).filter((b) => b.name.endsWith('-bus')).length).toBe(1);
  // one Undo takes the whole second send away
  await page.locator('#btn-undo').click();
  await expect.poll(async () => (await blocks()).some((b) => b.name === 'piano-send-reverb')).toBe(false);
  expect((await blocks()).some((b) => b.name === 'reverb-bus')).toBe(true);
});
