import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The Jam of a voice of a sequencer pattern shows the other voices of that pattern as outlines in their own colour, and the head of the pattern opens the Jam on the first voice.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\nchannel pad notes wave=triangle\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96 2:0:1:67:96\n  bass 1:0:2:36:90\npattern chorus bars=1\n  lead 1:0:1:72:96\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\nsequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=lead lane2=bass\n}\n',
  files: { 'sequences/a.seq': SEQ },
});
test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(2);
});

test('the Jam of a voice gets the other voices with notes as outlines behind it', async ({ page }) => {
  await page.locator('#sq-patterns .sqgridrow[data-voice="lead"] .sqlite').click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('data-ghosts', '1');   // bass has notes in this pattern, pad has none
});

test('the head of the pattern opens the Jam on the first voice with notes', async ({ page }) => {
  await page.locator('#sq-jam-all').click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect(panel).toHaveAttribute('data-block', /sq-lead/);
  await expect(panel).toHaveAttribute('data-ghosts', '1');
});

test('a pattern without any notes cannot open a Jam from its head', async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await page.locator('#sq-pattern-new').click();
  await page.locator('dialog[open] input').fill('empty');
  await page.locator('dialog[open] button', { hasText: 'OK' }).click();
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('empty');
  await expect(page.locator('#sq-jam-all')).toBeDisabled();
});
