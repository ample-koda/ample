import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// A song delivers several finals; each is rendered on purpose (estimate and disk check first), cut from the master with its fades
// and exported next to the other renders. The real server renders here, so this is the whole path.
const SONG = [
  'composition "finals" { ample 1  tempo 120  master-seed 1 }',
  'sequencer sq at 1 for 4 {',
  '  sequence {',
  '    sequencer 1',
  '    channel b bass wave=saw vol=-6',
  '    pattern p bars=1',
  '      b 1:0:4:45:100',
  '    playlist',
  '      clip p track=1 at=1 for=4',
  '  }',
  '}',
  'master master {',
  '  target -14 LUFS',
  '  limiter -1 dB',
  '  from sq.audio -> audio',
  '}',
  '',
].join('\n');
freshServer({ files: { 'finals-song.amplet': SONG } });

test('finals: define a teaser, estimate, render both finals and see the exports', async ({ page }) => {
  test.setTimeout(180_000);
  await openApp(page);
  const song = 'finals-song.amplet';
  await page.evaluate((path) => import('/app/store.js').then((m) => { m.S.path = path; }), song);

  // an implicit full final, then a teaser of two bars with fades as a wav
  const first = await api(`/api/finals?song=${encodeURIComponent(song)}`);
  expect(first.finals.map((f: { name: string }) => f.name)).toEqual(['full']);
  await api('/api/finals', { method: 'POST', body: { song, name: 'teaser', fromBar: 1, bars: 2, fadeInBeats: 1, fadeOutBars: 1, format: 'wav' } });
  await api('/api/finals', { method: 'POST', body: { song, name: 'full', fromBar: 1, bars: 0, format: 'wav' } });

  const estimate = await api(`/api/build/estimate?path=${encodeURIComponent(song)}`);
  expect(estimate.ok).toBe(true);
  expect(estimate.text).toMatch(/block\(s\) to render|Nothing to render/);

  const teaser = await api('/api/finals/render', { method: 'POST', body: { song, name: 'teaser' } });
  expect(teaser.path).toMatch(/^renders\/.*-teaser\.wav$/);
  expect(teaser.bytes).toBeGreaterThan(1000);
  const full = await api('/api/finals/render', { method: 'POST', body: { song, name: 'full' } });
  expect(full.bytes).toBeGreaterThan(teaser.bytes);
  expect(full.seconds).toBeGreaterThan(teaser.seconds);

  // the page lists both with their last render
  await page.evaluate(() => import('/app/projects.js').then((m) => m.openProjects('finals')));
  await expect(page.locator('#fin-list [data-final="teaser"]')).toContainText('renders/');
  await expect(page.locator('#fin-list [data-final="full"]')).toContainText('renders/');

  // an unknown final says so
  const missing = await api('/api/finals/render', { method: 'POST', body: { song, name: 'nope' } });
  expect(JSON.stringify(missing)).toContain('AMP5605');
});
