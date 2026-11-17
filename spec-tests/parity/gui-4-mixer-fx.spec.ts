import { test, expect, type Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// GUI flow 4, effects: the blocks between a source and its mixer input are chips under the fader; add, bypass, reorder and remove are ordinary song edits (Undo works).
freshServer();
test.beforeEach(async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('#btabs button[data-t="mixer"]').click();
  await expect(page.locator('#b-mixer .mixview')).toBeVisible();
});
const blocks = async () => (await api('/api/doc?path=song.amplet')).blocks as { name: string; parameters: { name: string; value: string }[] }[];
const lane = (page: Page, n: number) => page.locator('#b-mixer .mixstrip[data-block="mix"] .mixchan').nth(n);

test('the + adds an effect between the source and the mixer input, bypass writes enabled, effects can be moved and removed, Undo works', async ({ page }) => {
  await lane(page, 1).getByRole('button', { name: 'mix input 2 add effect' }).click();
  await page.locator('#menu').getByText('Compressor').click();
  await expect(lane(page, 1).locator('.mixfxchip')).toHaveCount(1);
  await expect(lane(page, 1).locator('.mixfxname')).toHaveText('bass-compressor');
  expect((await blocks()).some((b) => b.name === 'bass-compressor')).toBe(true);
  await expect(lane(page, 1).locator('.mixsrc')).toHaveText('bass');   // the name is still the instrument, not the effect
  await lane(page, 1).getByRole('button', { name: 'bass-compressor bypass' }).click();
  await expect.poll(async () => String((await blocks()).find((b) => b.name === 'bass-compressor')?.parameters.find((p) => p.name === 'enabled')?.value ?? '')).toContain('off');
  await expect(lane(page, 1).locator('.mixfxchip.off')).toHaveCount(1);
  await lane(page, 1).getByRole('button', { name: 'mix input 2 add effect' }).click();
  await page.locator('#menu').getByText('Reverb').click();
  await expect(lane(page, 1).locator('.mixfxname')).toHaveText(['bass-compressor', 'bass-reverb']);
  await lane(page, 1).locator('[data-fx="bass-reverb"]').hover();
  await lane(page, 1).getByRole('button', { name: 'bass-reverb earlier' }).click();
  await expect(lane(page, 1).locator('.mixfxname')).toHaveText(['bass-reverb', 'bass-compressor']);
  await lane(page, 1).locator('[data-fx="bass-reverb"]').hover();
  await lane(page, 1).getByRole('button', { name: 'bass-reverb remove' }).click();
  await expect(lane(page, 1).locator('.mixfxname')).toHaveText(['bass-compressor']);
  // every edit of this test is one undo step; the button still reads enabled while the last step is being written, so a loop that
  // reads it between clicks clicked once too often and waited forever: count the steps of the store instead
  const undoLeft = () => page.evaluate(() => import('/app/store.js').then((s) => s.S.undo.length));
  for (let left = await undoLeft(); left > 0; left--) { await page.locator('#btn-undo').click(); await expect.poll(undoLeft).toBe(left - 1); }
  await expect(lane(page, 1).locator('.mixfxchip')).toHaveCount(0);
});
