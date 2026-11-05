import { expect, test } from '@playwright/test';
import fs from 'node:fs';
import { api, freshServer, launchUrl, openApp } from '../../web/tests/support';
import { bigSong } from '../../web/tests/playlist-fixtures';

// its own server: the song of this file is rewritten, and the other Playlist specs must not see it
freshServer();

test('playlist perf: a song of 100+ blocks and 200 bars draws within its budget, closed and fully open', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: bigSong(), baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
  await expect(page.locator('#b-playlist .pl-line')).toHaveCount(16);
  const ms = async () => Number(await page.locator('#b-playlist').getAttribute('data-render-ms'));
  const closed = await ms();
  await page.locator('#b-playlist .pl-toolbar button', { hasText: 'Open all' }).click();
  await expect(page.locator('#b-playlist .pl-sub')).not.toHaveCount(0);
  const open = await ms();
  const rows = await page.locator('#b-playlist .pl-row').count();
  const clips = await page.locator('#b-playlist .pl-clip').count();
  console.log('PERF>>', JSON.stringify({ closed, open, rows, clips }));
  const budget = JSON.parse(fs.readFileSync('docs/perf-budgets.json', 'utf8')).browserBudgetsMilliseconds;
  expect(closed).toBeLessThan(budget.playlistFirstDrawClosed);
  expect(open).toBeLessThan(budget.playlistFirstDrawOpen);
  await page.locator('#b-playlist .pl-toolbar button', { hasText: '+' }).last().click();
  await expect.poll(ms).toBeGreaterThanOrEqual(0);
  expect(await ms()).toBeLessThan(budget.playlistZoom);
  const before = (await page.evaluate(() => (window as any).AMPLE_STATE.doc.text)) as string;
  const started = Date.now();
  const clip = page.locator('#b-playlist .pl-clip').first();
  await clip.focus();
  await clip.press('ArrowRight');
  await expect.poll(async () => (await page.evaluate(() => (window as any).AMPLE_STATE.doc.text)) !== before).toBe(true);
  expect(Date.now() - started).toBeLessThan(budget.playlistMoveAClip);
});

test('playlist minimap: it shows the whole song, and a click on it moves the view', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('ample.playlist.on', '1'));
  await openApp(page);
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text: bigSong(), baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#btabs button[data-t="playlist"]').click();
  const minimap = page.locator('#b-playlist .pl-minimap');
  await expect(minimap).toBeVisible();
  const scroll = page.locator('#b-playlist .pl-scroll');
  await expect.poll(() => scroll.evaluate((e) => e.scrollWidth > e.clientWidth)).toBe(true);
  expect(await scroll.evaluate((e) => e.scrollLeft)).toBe(0);
  const box = (await minimap.boundingBox())!;
  await page.mouse.click(box.x + box.width * 0.9, box.y + box.height / 2);
  await expect.poll(() => scroll.evaluate((e) => e.scrollLeft)).toBeGreaterThan(300);
  await minimap.focus();
  const at = await scroll.evaluate((e) => e.scrollLeft);
  await page.keyboard.press('ArrowLeft');
  await expect.poll(() => scroll.evaluate((e) => e.scrollLeft)).toBeLessThan(at);
});
