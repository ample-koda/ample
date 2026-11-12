import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 7: the sequencer opens with the whole song in view and the Arrangement panel folded away (it comes back when you leave), the wheel scrolls the playlist sideways and Ctrl + wheel zooms.
const clips = Array.from({ length: 12 }, (_, i) => `  clip ${i % 2 ? 'chorus' : 'verse'} track=1 at=${1 + i * 4} for=4`).join('\n');
const SEQ = `sequencer 1\nchannel lead notes wave=square\npattern verse bars=4\n  lead 1:0:1:60:96\npattern chorus bars=4\n  lead 1:0:1:72:96\nplaylist\n${clips}\n`;
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 48 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq .sqclip')).toHaveCount(12);
});

test('the whole song is in view when the sequencer opens', async ({ page }) => {
  await expect.poll(() => page.locator('#sq-playlist .sqscroll').evaluate((el) => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(2);
});

test('the Arrangement panel folds away while the sequencer is open and comes back after it', async ({ page }) => {
  await expect(page.locator('#bottom')).toHaveClass(/min/);
  await page.locator('#sq-undoback').click();
  await expect(page.locator('#sq')).toBeHidden();
  await expect(page.locator('#bottom')).not.toHaveClass(/min/);
});

test('Ctrl + wheel zooms the playlist and the wheel scrolls it sideways', async ({ page }) => {
  const tick = () => page.locator('#sq-playlist .sqtick').first().evaluate((el) => (el as HTMLElement).getBoundingClientRect().width);
  const fitted = await tick();
  await page.locator('#sq-playlist .sqscroll').hover();
  await page.keyboard.down('Control');
  await page.mouse.wheel(0, -300);
  await page.keyboard.up('Control');
  await expect.poll(tick).toBeGreaterThan(fitted);
  await page.locator('#sq-playlist .sqscroll').hover();
  await page.mouse.wheel(0, 400);
  await expect.poll(() => page.locator('#sq-playlist .sqscroll').evaluate((el) => el.scrollLeft)).toBeGreaterThan(0);
});
