import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 2: PAT / SONG next to Play. SONG is what Play always did; PAT repeats the chosen pattern from its first place in the playlist.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96\npattern chorus bars=1\n  lead 1:0:1:72:96\nplaylist\n  clip verse track=1 at=1 for=2\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.evaluate(() => { try { localStorage.removeItem('ample.sq.playMode'); } catch { /* none */ } });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(2);
});

test('SONG is on to begin with, and the buttons switch and remember the mode', async ({ page }) => {
  await expect(page.locator('#sq-mode-song')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#sq-mode-pat')).toHaveAttribute('aria-pressed', 'false');
  await page.locator('#sq-mode-pat').click();
  await expect(page.locator('#sq-mode-pat')).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('#sq-mode-song')).toHaveAttribute('aria-pressed', 'false');
  expect(await page.evaluate(() => localStorage.getItem('ample.sq.playMode'))).toBe('pattern');
  await page.locator('#sq-mode-song').click();
  expect(await page.evaluate(() => localStorage.getItem('ample.sq.playMode'))).toBe('song');
});

test('PAT says what it does in the explanation', async ({ page }) => {
  await page.locator('#sq-mode-pat').click();
  await expect(page.locator('#sq-hint')).toContainText('PAT: the pattern verse repeats');
  await page.locator('#sq-mode-song').click();
  await expect(page.locator('#sq-hint')).toContainText('The whole song repeats');
});

test('PAT with a pattern that is in no clip of the playlist says where to put it', async ({ page }) => {
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click();
  await page.locator('#sq-mode-pat').click();
  await page.locator('#sq-play').click();
  await expect(page.locator('#toast')).toContainText('Put the pattern "chorus" in the playlist first');
});
