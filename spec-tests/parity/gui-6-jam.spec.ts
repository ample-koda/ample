import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 6: the Jam of a voice names the voices behind its notes in their colours, and every tool button shows its key.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96\n  bass 1:0:2:36:90\nplaylist\n  clip verse track=1 at=1 for=2\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  await expect(page.locator('dialog.mls-panel canvas').first()).toBeVisible();
});

test('the legend names the other voices of the pattern', async ({ page }) => {
  const legend = page.locator('dialog.mls-panel .mls-legend');
  await expect(legend).toBeVisible();
  await expect(legend).toContainText('bass');
  await expect(legend).not.toContainText('lead');
});

test('every tool button shows its key', async ({ page }) => {
  const keys = await page.locator('dialog.mls-panel .mls-tool .mls-toolkey').allTextContents();
  expect(keys.length).toBeGreaterThanOrEqual(5);
  for (const key of keys) expect(key).toMatch(/^[A-Z0-9]$/);
});
