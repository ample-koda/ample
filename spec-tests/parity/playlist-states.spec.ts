import { expect, test, type Page } from '@playwright/test';
import { api, dialog, freshServer, launchUrl, openApp } from '../../web/tests/support';

// its own server: it writes game/score.json, and openApp of the other specs rewrites the song of the shared one
freshServer();


const score = {
  version: 1, name: 'demo-score', song: 'song.amplet', loopFromBar: 1, loopBars: 8, initialState: 'calm',
  layers: [{ id: 'drums', block: 'drums' }, { id: 'bass', block: 'bass' }, { id: 'gone', block: 'not-in-the-song' }],
  stingers: [],
  states: [{ id: 'calm', levels: { drums: 1 } }, { id: 'tension', levels: { drums: 1, bass: 0.8 }, fallback: 'calm' }],
  transitions: { default: { quantize: 'bar', fadeBars: 1 }, rules: [] },
  variants: [],
};

async function writeScore(text: string) {
  await api('/api/file', { method: 'PUT', body: { path: 'game/score.json', text } });
}

async function openPlaylist(page: Page) {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  await page.locator('#btabs button[data-t="playlist"]').click();
  await expect(page.locator('#b-playlist .pl-line').first()).toBeVisible();
}

test('playlist states: the matrix shows the levels, and a level is written to the score file', async ({ page }) => {
  await writeScore(`${JSON.stringify(score, null, 2)}\n`);
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'States' }).click();
  const matrix = page.locator('#b-playlist .pl-states');
  await expect(matrix).toContainText('drums');
  await expect(matrix).toContainText('block not-in-the-song is not in the song');
  await matrix.getByRole('button', { name: 'Level of bass in calm: 0' }).click();
  await dialog(page, 'Set', ['0.5']);
  await expect(matrix.getByRole('button', { name: 'Level of bass in calm: 0.5' })).toBeVisible();
  const file = await api('/api/file?path=game/score.json');
  expect(JSON.parse(file.text).states[0].levels).toEqual({ drums: 1, bass: 0.5 });
  expect(file.text.startsWith('{\n  "version": 1,')).toBe(true);
});

test('playlist states: a level outside 0 to 1 is refused and the file stays', async ({ page }) => {
  await writeScore(`${JSON.stringify(score, null, 2)}\n`);
  await openPlaylist(page);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'States' }).click();
  await page.locator('#b-playlist .pl-states').getByRole('button', { name: 'Level of bass in calm: 0' }).click();
  await dialog(page, 'Set', ['3']);
  await expect(page.locator('#b-playlist .pl-states').getByRole('button', { name: 'Level of bass in calm: 0' })).toBeVisible();
  const file = await api('/api/file?path=game/score.json');
  expect(JSON.parse(file.text).states[0].levels).toEqual({ drums: 1 });
});

test('playlist states: the overlay dims the lines that are off in a state and matches the file', async ({ page }) => {
  await writeScore(`${JSON.stringify(score, null, 2)}\n`);
  await openPlaylist(page);
  await page.getByLabel('State overlay: dim the lines that are off in a state').selectOption('calm');
  await expect(page.locator('#b-playlist .pl-line', { hasText: 'bass' })).toHaveClass(/state-off/);
  await expect(page.locator('#b-playlist .pl-line', { hasText: 'drums' })).not.toHaveClass(/state-off/);
  await page.getByLabel('State overlay: dim the lines that are off in a state').selectOption('tension');
  await expect(page.locator('#b-playlist .pl-line', { hasText: 'bass' })).not.toHaveClass(/state-off/);
});
