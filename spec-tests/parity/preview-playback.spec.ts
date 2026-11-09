import { test, expect } from '@playwright/test';
import { bottomTab, node, openApp } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

const playerState = (page) => page.evaluate(() => (window as any).AMPLE_PLAYER.state());

async function build(page) {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await expect(node(page, 'drums').locator('.player')).toBeVisible();
}

test('after a build the audio blocks have a player and the songs shows its waveform', async ({ page }) => {
  await bottomTab(page, 'arr');
  await expect(page.locator('#tp-play')).toBeDisabled();
  await build(page);
  await expect(page.locator('#tp-play')).toBeEnabled();
  await expect(page.locator('#btn-preview')).toHaveAttribute('aria-label', 'Play');   // the button shows only the triangle
  await expect(node(page, 'chords').locator('.player')).toHaveCount(0);
  const drawn = await page.evaluate(() => { const c = document.querySelector('#tp-wave canvas') as HTMLCanvasElement; return new Promise<boolean>((res) => setTimeout(() => res(c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data.some((v) => v !== 0)), 600)); });
  expect(drawn).toBe(true);
});

test('a block plays, pauses, and the audio is streamed with range support', async ({ page }) => {
  const heads: string[] = [];
  page.on('response', (r) => { if (r.url().includes('/api/data/')) heads.push(r.headers()['accept-ranges'] || ''); });
  await build(page);
  const play = node(page, 'drums').locator('.player > button').first();   // its title changes to Pause while playing
  await play.click();
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await expect.poll(async () => (await playerState(page)).time).toBeGreaterThan(0.2);
  await play.click();
  await expect.poll(async () => (await playerState(page)).playing).toBe(false);
  expect(heads.some((h) => h === 'bytes')).toBe(true);
});

test('node playback shows a scrubable waveform and the header stop rewinds every take', async ({ page }) => {
  await build(page);
  const player = node(page, 'drums').locator('.player');
  const wave = player.locator('.plbg');
  await expect(wave).toBeVisible();
  await expect.poll(async () => wave.locator('canvas').evaluate(canvas => (canvas as HTMLCanvasElement).getContext('2d')!.getImageData(0, 0, 320, 36).data.some(value => value !== 0))).toBe(true);
  // a quarter of the way in, whatever width the node gives the waveform
  const waveBox = (await wave.boundingBox())!;
  await wave.click({ position: { x: Math.round(waveBox.width * 0.25), y: Math.round(waveBox.height / 2) } });
  await expect.poll(async () => { const state = await playerState(page); return state.duration ? state.time / state.duration : 0; }).toBeGreaterThan(0.15);
  await expect.poll(async () => (await playerState(page)).playing).toBe(false);
  await player.locator('> button').first().click();
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await expect(page.locator('#btn-stop')).toHaveClass(/playing/);
  const bounds = (await wave.boundingBox())!;
  await page.mouse.move(bounds.x + bounds.width * 0.2, bounds.y + bounds.height / 2);
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width * 0.75, bounds.y + bounds.height / 2, { steps: 4 });
  await page.mouse.up();
  await expect.poll(async () => { const state = await playerState(page); return state.duration ? state.time / state.duration : 0; }).toBeGreaterThan(0.65);
  await page.locator('#btn-stop').click();
  await expect.poll(async () => (await playerState(page)).time).toBe(0);
  await expect(page.locator('#btn-stop')).toBeDisabled();
  await expect(page.locator('#btn-stop')).not.toHaveClass(/playing/);
});

test('seek and loop work on the built song', async ({ page }) => {
  await bottomTab(page, 'arr');
  await build(page);
  await page.click('#tp-loop');
  expect((await playerState(page)).loop).toBe(true);
  await page.click('#tp-play');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  const w = (await page.locator('#tp-wave').boundingBox())!;
  await page.mouse.click(w.x + w.width * 0.8, w.y + 10);
  await expect.poll(async () => { const s = await playerState(page); return s.duration > 0 ? s.time / s.duration : 0; }).toBeGreaterThan(0.6);
  await page.locator('#transport button[title*="Stop or download"]').click();
  await page.locator('#menu').getByText('Stop', { exact: true }).click();
  await expect.poll(async () => (await playerState(page)).time).toBe(0);
});

test('Preview song builds what is missing and plays the song', async ({ page }) => {
  await page.click('#btn-preview');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await expect(node(page, 'master').locator('.node-status')).toBeVisible();
});

test('a chosen bar range can be previewed', async ({ page }) => {
  await bottomTab(page, 'arr');
  await expect(async () => {   // the ruler is drawn again while the tab opens and a drag that began before that chooses nothing: drag again until the Preview button shows
    const r = (await page.locator('#arr .ruler').boundingBox())!;
    await page.mouse.move(r.x + 40, r.y + 8);
    await page.mouse.down();
    await page.mouse.move(r.x + 140, r.y + 8, { steps: 3 });
    await page.mouse.up();
    await expect(page.locator('#arr-preview')).toBeVisible({ timeout: 2000 });
  }).toPass({ timeout: 20000 });
  await page.click('#arr-preview');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
});
