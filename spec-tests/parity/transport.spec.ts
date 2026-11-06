import { test, expect } from '@playwright/test';
import { bottomTab, node, openApp } from '../../web/tests/support';

// The header transport: one Play that builds when needed, one Stop, Space and Ctrl+B, loop, the chosen bars, the failed badge and the audio download.
test.beforeEach(async ({ page }) => { await openApp(page); });

const playerState = (page) => page.evaluate(() => (window as any).AMPLE_PLAYER.state());

test('Play builds first when the song is out of date and just plays when it is built; Stop is one button', async ({ page }) => {
  await expect(page.locator('#btn-preview')).toHaveText('▶ Build and play');
  await expect(page.locator('#btn-preview')).toHaveAttribute('title', /Out of date|not built/);
  await expect(page.locator('#btn-stop')).toBeDisabled();
  await page.click('#btn-preview');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await expect(page.locator('#btn-preview')).toHaveAttribute('aria-label', 'Play');   // the button shows only the triangle
  await expect(page.locator('#b-playing')).toHaveText('▶ the song');
  await expect(page.locator('#btn-stop')).toHaveAttribute('data-mode', 'audio');
  await page.click('#btn-stop');
  await expect.poll(async () => (await playerState(page)).playing).toBe(false);
  await expect(page.locator('#b-playing')).toBeHidden();
});

test('Space plays and stops, Ctrl+B builds, and typing in a field keeps the space', async ({ page }) => {
  await page.keyboard.press('Control+b');
  await expect(page.locator('#btxt')).toHaveText('idle');
  await expect(node(page, 'drums').locator('.player')).toBeVisible();
  await page.locator('#lib-q').click();
  await page.keyboard.type('a b');
  await expect(page.locator('#lib-q')).toHaveValue('a b');
  expect((await playerState(page)).playing).toBe(false);
  await page.locator('#lib-q').fill('');
  await page.locator('body').click({ position: { x: 5, y: 400 } });
  await page.keyboard.press('Space');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await page.keyboard.press('Space');
  await expect.poll(async () => (await playerState(page)).playing).toBe(false);
});

test('the Build button says that unsaved changes are built too', async ({ page }) => {
  await expect(page.locator('#btn-build')).toHaveAttribute('title', /Unsaved changes are built too/);
});

test('Loop is in the header, the chosen bars can be played from it, and the song audio is in the file menu @quarantine', async ({ page }) => {
  await page.click('#btn-play-options');   // Loop moved into the play options menu
  await page.click('#btn-loop');
  await expect.poll(async () => (await playerState(page)).loop).toBe(true);
  await expect(page.locator('#btn-loop')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#btn-play-range')).toBeHidden();
  await bottomTab(page, 'arr');
  const r = (await page.locator('#arr .ruler').boundingBox())!;
  await page.mouse.move(r.x + 40, r.y + 8);
  await page.mouse.down();
  await page.mouse.move(r.x + 140, r.y + 8, { steps: 3 });
  await page.mouse.up();
  await expect(page.locator('#btn-play-range')).toBeVisible();
  await expect(page.locator('#btn-play-range')).toContainText('bars');
  await page.click('#btn-play-range');
  await expect.poll(async () => (await playerState(page)).playing).toBe(true);
  await page.click('#btn-stop');
  await page.locator('#save-ctl-top button').last().click();
  await expect(page.locator('#menu div', { hasText: 'Download the song audio (WAV)' })).toBeVisible();
});
