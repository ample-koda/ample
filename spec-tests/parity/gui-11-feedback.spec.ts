import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 11: every channel has a light, and a bar along the top of the page moves while a build runs.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\npattern verse bars=2\n  lead 1:0:1:60:96\n  bass 1:0:2:36:90\nplaylist\n  clip verse track=1 at=1 for=2\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
});

test('every channel of the rack has a light that is out while nothing plays', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(2);
  await expect(page.locator('#sq-rack .sqlight')).toHaveCount(2);
  await expect(page.locator('#sq-rack .sqlight.on')).toHaveCount(0);
});

test('the build bar is hidden to begin with and moves while a build runs', async ({ page }) => {
  const bar = page.locator('#build-progress');
  await expect(bar).toHaveCount(1);
  await expect(bar).not.toHaveClass(/on/);
  await page.evaluate(async () => { const s = await import('/app/store.js'); s.S.build.running = true; s.notify(); });
  await expect(bar).toHaveClass(/on/);
  await page.evaluate(async () => { const s = await import('/app/store.js'); s.S.build.running = false; s.notify(); });
  await expect(bar).not.toHaveClass(/on/);
});
