import { test, expect } from '@playwright/test';
import { node, openApp } from '../../web/tests/support';

// Switching quickly between takes while a play is still loading: the last request wins, the earlier one is cancelled and no player is left stuck (playVersion in web/app/player.js).

test.beforeEach(async ({ page }) => { await openApp(page); });

async function builtTakes(page) {
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await expect(node(page, 'drums').locator('.player')).toBeVisible();
  const takes = await page.evaluate(() => (window as any).AMPLE_STATE.build.takes as Record<string, string>);
  expect(takes.drums).toBeTruthy();
  expect(takes.bass).toBeTruthy();
  return takes;
}

const slowAudio = (page, ms: number) => page.route('**/api/data/**', async (route) => { await new Promise((r) => setTimeout(r, ms)); await route.continue(); });
const state = (page) => page.evaluate(() => (window as any).AMPLE_PLAYER.state());

test('a second Play while the first is still loading wins, and the first never starts', async ({ page }) => {
  const takes = await builtTakes(page);
  await slowAudio(page, 1200);
  await page.evaluate(async ([a, b]) => {
    const player = await import('/app/player.js');
    player.stop();
    void player.play(a);
    await new Promise((r) => setTimeout(r, 150));
    void player.play(b);
  }, [takes.drums, takes.bass]);
  await expect.poll(async () => (await state(page)).loading, { timeout: 20000 }).toBeNull();
  const final = await state(page);
  expect(final.take).toBe(takes.bass);
  await page.evaluate(async () => (await import('/app/player.js')).stop());
  expect((await state(page)).playing).toBe(false);
});

test('a Stop while a Play is loading leaves nothing playing and nothing pending', async ({ page }) => {
  const takes = await builtTakes(page);
  await slowAudio(page, 1200);
  await page.evaluate(async (a) => {
    const player = await import('/app/player.js');
    void player.play(a);
    await new Promise((r) => setTimeout(r, 150));
    player.stop();
  }, takes.drums);
  await page.waitForTimeout(2500);
  const final = await state(page);
  expect(final.playing).toBe(false);
  expect(final.loading).toBeNull();
});

test('rapid switching between two takes ends on the last one with a single player', async ({ page }) => {
  const takes = await builtTakes(page);
  await page.evaluate(async ([a, b]) => {
    const player = await import('/app/player.js');
    for (let i = 0; i < 6; i++) { void player.play(i % 2 ? a : b); await new Promise((r) => setTimeout(r, 40)); }
  }, [takes.drums, takes.bass]);   // the last call (i = 5) plays drums
  await expect.poll(async () => (await state(page)).loading, { timeout: 20000 }).toBeNull();
  const final = await state(page);
  expect(final.take).toBe(takes.drums);
  expect(await page.evaluate(() => document.querySelectorAll('audio').length)).toBeLessThanOrEqual(1);
  await page.evaluate(async () => (await import('/app/player.js')).stop());
});
