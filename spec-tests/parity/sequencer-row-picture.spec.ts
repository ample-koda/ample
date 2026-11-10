import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// A row of notes in the sequencer workspace shows a small picture of its notes next to the count; a row without notes is dimmed; the Jam button is an icon, and the count
// and the picture open the Jam as well.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96 2:0:1:67:96\n  bass 1:0:2:36:90\npattern chorus bars=1\n  lead 1:0:1:72:96\n';
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
const row = (page, voice: string) => page.locator(`#sq-patterns .sqgridrow[data-voice="${voice}"]`);

test('a row draws one bar in its picture for every note', async ({ page }) => {
  await expect(row(page, 'lead').locator('.sqthumb rect')).toHaveCount(3);
  await expect(row(page, 'bass').locator('.sqthumb rect')).toHaveCount(1);
  await expect(row(page, 'lead')).toContainText('3 notes');
});

test('a row without notes in this pattern is dimmed and says so', async ({ page }) => {
  await expect(row(page, 'bass')).not.toHaveClass(/silent/);
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click();
  await expect(row(page, 'bass')).toHaveClass(/silent/);
  await expect(row(page, 'bass')).toContainText('0 notes');
  await expect(row(page, 'bass').locator('.sqthumb rect')).toHaveCount(0);
});

test('the Jam button is an icon with its name, and the picture opens the Jam too', async ({ page }) => {
  const button = row(page, 'lead').locator('.sqlite');
  await expect(button).toHaveText('');
  await expect(button).toHaveAttribute('aria-label', 'AMPLE Jam');
  await expect(button.locator('img.jam-icon')).toHaveAttribute('src', /jam-small.svg/);   // the J variant of the logo
  await row(page, 'lead').locator('.sqthumbwrap').click();
  await expect(page.locator('dialog.mls-panel canvas').first()).toBeVisible();
  await page.getByRole('button', { name: 'Close AMPLE Jam' }).click();
  await expect(page.locator('dialog.mls-panel')).toBeHidden();
  await row(page, 'lead').locator('.sqopen').click();   // the count opens it as well
  await expect(page.locator('dialog.mls-panel canvas').first()).toBeVisible();
});
