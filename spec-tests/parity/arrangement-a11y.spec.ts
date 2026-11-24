import { test, expect } from '@playwright/test';
import { bottomTab, openApp } from '../../web/tests/support';

test.beforeEach(async ({ page }) => {
  await openApp(page);
  await bottomTab(page, 'arr');
  await page.evaluate(() => document.querySelector<HTMLElement>('#bottom')!.style.setProperty('--bh', '560px'));
});

const focusedBar = (page: import('@playwright/test').Page) => page.evaluate(() => {
  const node = document.activeElement as HTMLElement | null;
  return node?.matches('#arr .bar[data-block]') ? `${node.dataset.block}:${node.dataset.part}` : (node?.id || node?.className || node?.tagName || '');
});

test('a clip keeps the keyboard focus when it is moved with the arrow keys, and its name says what it is', async ({ page }) => {
  const clip = page.locator('#arr .bar[data-block="piano"]').first();
  await expect(clip).toHaveAttribute('aria-label', /Ctrl\+Enter opens its player/);
  const before = await clip.getAttribute('aria-label');
  await clip.focus();
  await clip.press('ArrowRight');
  // the arrangement is drawn again after the change: the same clip has the focus again and has moved one bar
  await expect.poll(async () => (await page.locator('#arr .bar[data-block="piano"]').first().getAttribute('aria-label')) !== before).toBe(true);
  await expect.poll(() => focusedBar(page)).toBe('piano:0');
  await page.keyboard.press('ArrowRight');
  await expect.poll(() => focusedBar(page)).toBe('piano:0');
});

test('the bars to preview are chosen with the keyboard on the ruler, and said aloud', async ({ page }) => {
  const ruler = page.locator('#arr .ruler');
  await expect(ruler).toHaveAttribute('tabindex', '0');
  await ruler.focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#arr-range-status')).toHaveText('Bar 1 selected');
  await page.keyboard.press('ArrowRight');
  await expect(page.locator('#arr-range-status')).toHaveText('Bar 2 selected');
  await page.keyboard.press('Shift+ArrowRight');
  await expect(page.locator('#arr-range-status')).toHaveText('Bars 2 to 3 selected');
  await expect(page.locator('#arr-preview')).toContainText('bars 2-3');
  await expect(page.locator('#arr .ruler')).toBeFocused();
  await expect(page.getByRole('button', { name: 'Clear the selected bars' })).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('#arr-preview')).toHaveCount(0);
  await expect(page.locator('#arr .ruler')).toBeFocused();
});

test('the transport waveform is a position slider and the playback menu button has a name', async ({ page }) => {
  const wave = page.locator('#tp-wave');
  await expect(wave).toHaveAttribute('role', 'slider');
  await expect(wave).toHaveAttribute('aria-label', 'Position in the built song');
  await expect(wave).toHaveAttribute('aria-valuemin', '0');
  await expect(wave).toHaveAttribute('aria-valuemax', '100');
  await expect(wave).toHaveAttribute('aria-valuetext', /./);
  await expect(page.getByRole('button', { name: 'Stop or download the built song' })).toHaveAttribute('aria-haspopup', 'menu');
});

test('Ctrl+Enter opens the player of a block and the keys inside it do not move the clip', async ({ page }) => {
  const clip = page.locator('#arr .bar[data-block="piano"]').first();
  await clip.focus();
  await clip.press('Control+Enter');
  await expect(page.locator('#arr .track.open')).toHaveCount(1);
  const slider = clip.locator('.bar-wv');
  await expect(slider).toHaveAttribute('role', 'slider');
  await expect(slider).toHaveAttribute('aria-label', /Position in/);
  const before = await clip.getAttribute('aria-label');
  await slider.focus();
  await slider.press('ArrowRight');
  await expect(clip).toHaveAttribute('aria-label', before!);
  await clip.focus();
  await clip.press('Control+Enter');
  await expect(page.locator('#arr .track.open')).toHaveCount(0);
});
