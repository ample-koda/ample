import { expect, test } from '@playwright/test';
import { api, freshServer, launchUrl, openApp } from '../../web/tests/support';

// its own server: the song of this file is rewritten, and the other Playlist specs must not see it
freshServer();

test('groups without a place in the song are drawn, with no NaN in the canvas', async ({ page }) => {
  const errors: string[] = [];
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', (e) => errors.push(String(e)));
  await openApp(page);
  const text = `${process.env.AMPLE_DEMO_SONG}\nview {\n  group Rhythm {\n    title "Rhythm"\n    member drums\n    member bass\n  }\n  group Keys {\n    title "Keys"\n    member piano\n    member chords\n    x 300\n    y -100\n  }\n}\n`;
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc', { method: 'PUT', body: { path: 'song.amplet', text, baseVersion: doc.version } });
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.goto(launchUrl());
  await page.waitForSelector('body[data-ready="1"]');
  await expect(page.locator('.node[data-id="Rhythm"]')).toBeVisible();
  const places = await page.locator('.node').evaluateAll((nodes) => nodes.map((n) => [(n as HTMLElement).style.left, (n as HTMLElement).style.top]));
  expect(places.flat().some((p) => /NaN/.test(p))).toBe(false);
  await page.waitForTimeout(800);
  expect(errors.filter((e) => /NaN/.test(e))).toEqual([]);
});
