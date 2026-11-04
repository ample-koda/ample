import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// Audio files in the project panel: the tile has inline Play / Loop and a waveform to scrub, the detail row grows a waveform strip while it plays.
// Scrubbing must move the audio and must not shift the layout (the name, the other tiles and rows stay where they are).
freshServer();

const PATH = 'samples/scrub-me.wav';
const openPanel = async (page: Page) => { await page.getByRole('button', { name: 'Project', exact: true }).click(); await expect(page.locator('#b-project')).toBeVisible(); };
const state = (page: Page) => page.evaluate(() => (window as any).AMPLE_PLAYER.state());

test.beforeEach(async ({ page }) => {
  await openApp(page);
  await page.evaluate(async () => {
    const rate = 8000, samples = rate * 4, buffer = new ArrayBuffer(44 + samples * 2), v = new DataView(buffer);
    const text = (at: number, t: string) => [...t].forEach((c, i) => v.setUint8(at + i, c.charCodeAt(0)));
    text(0, 'RIFF'); v.setUint32(4, 36 + samples * 2, true); text(8, 'WAVEfmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true); text(36, 'data'); v.setUint32(40, samples * 2, true);
    for (let i = 0; i < samples; i++) v.setInt16(44 + i * 2, Math.round(9000 * Math.sin(i / 8)), true);
    const { api } = await import('/app/api.js');
    await api.uploadProject(new File([buffer], 'scrub-me.wav'), 'samples', 'overwrite', () => {});
  });
  await openPanel(page);
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
});

test('a tile plays inline, scrubbing moves the audio, and the layout of the tiles stays', async ({ page }) => {
  const tile = page.locator('.project-tile').filter({ hasText: 'scrub-me.wav' }).first();
  await expect(tile).toBeVisible();
  await tile.hover();
  const before = (await tile.boundingBox())!;
  const others = await page.locator('.project-tile').evaluateAll((nodes) => nodes.map((n) => { const r = n.getBoundingClientRect(); return [Math.round(r.x), Math.round(r.y)]; }));
  await tile.getByRole('button', { name: 'Play preview' }).click();
  await expect.poll(async () => (await state(page)).source, { timeout: 15000 }).toBe(PATH);
  await expect.poll(async () => (await state(page)).duration).toBeGreaterThan(1);

  const scrub = tile.locator('input[type=range]');
  await scrub.evaluate((input: HTMLInputElement) => { input.value = '750'; input.dispatchEvent(new Event('input', { bubbles: true })); });
  await expect.poll(async () => { const s = await state(page); return s.duration ? s.time / s.duration : 0; }, { timeout: 5000 }).toBeGreaterThan(0.6);

  const after = (await tile.boundingBox())!;
  expect([Math.round(after.width), Math.round(after.height)]).toEqual([Math.round(before.width), Math.round(before.height)]);
  const othersAfter = await page.locator('.project-tile').evaluateAll((nodes) => nodes.map((n) => { const r = n.getBoundingClientRect(); return [Math.round(r.x), Math.round(r.y)]; }));
  expect(othersAfter).toEqual(others);

  // moving away from the tile stops the preview: nothing keeps playing
  await page.mouse.move(5, 5);
  await expect.poll(async () => (await state(page)).playing, { timeout: 5000 }).toBe(false);
});

test('a detail row shows its waveform strip while it plays without moving the name or the other rows, and the strip scrubs', async ({ page }) => {
  await page.locator('.project-panel-toolbar button', { hasText: /^Details$/ }).click();
  const row = page.locator('tr[data-path="samples/scrub-me.wav"]').first();
  await expect(row).toBeVisible();
  const name = row.locator('td').first();
  const boxes = async () => ({ row: (await row.boundingBox())!, name: (await name.boundingBox())! });
  const before = await boxes();
  const tops = await page.locator('tr[data-path]').evaluateAll((nodes) => nodes.map((n) => Math.round(n.getBoundingClientRect().top)));
  await row.getByRole('button', { name: /Play/ }).first().click().catch(async () => { await page.evaluate(async (p) => (await import('/app/player.js')).playProjectFile(p), PATH); });
  await expect.poll(async () => (await state(page)).playing, { timeout: 15000 }).toBe(true);
  await expect(row.locator('.project-row-wave')).toBeVisible();
  const during = await boxes();
  expect(Math.round(during.row.height)).toBe(Math.round(before.row.height));
  expect(Math.round(during.name.x)).toBe(Math.round(before.name.x));
  expect(Math.round(during.name.y)).toBe(Math.round(before.name.y));
  expect(await page.locator('tr[data-path]').evaluateAll((nodes) => nodes.map((n) => Math.round(n.getBoundingClientRect().top)))).toEqual(tops);

  await row.locator('.project-row-wave input[type=range]').evaluate((input: HTMLInputElement) => { input.value = '500'; input.dispatchEvent(new Event('input', { bubbles: true })); });
  await expect.poll(async () => { const s = await state(page); return s.duration ? s.time / s.duration : 0; }, { timeout: 5000 }).toBeGreaterThan(0.4);
  await row.getByRole('button', { name: /Stop/ }).first().click();
  await expect.poll(async () => (await state(page)).playing).toBe(false);
  await expect.poll(async () => (await state(page)).source).toBeNull();
});
