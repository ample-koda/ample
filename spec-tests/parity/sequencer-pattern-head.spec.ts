import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The sequencer workspace says which pattern the rows belong to (a line with its name, bars and notes), shows the number of notes behind each tab, fills the chosen tab
// with its own colour and marks every row with the colour of its channel.
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

test('a line above the rows names the pattern with its bars and notes, and follows the tab that is chosen', async ({ page }) => {
  const head = page.locator('#sq-pattern-head');
  await expect(head).toContainText('verse');
  await expect(page.locator('#sq-pattern-summary')).toHaveText('2 bars · 4 notes');
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click();
  await expect(head).toContainText('chorus');
  await expect(page.locator('#sq-pattern-summary')).toHaveText('1 bar · 1 note');
});

test('every tab shows how many notes its pattern has, and its text stays the name', async ({ page }) => {
  const verse = page.locator('#sq-patterns .sqtab[data-pattern="verse"]'), chorus = page.locator('#sq-patterns .sqtab[data-pattern="chorus"]');
  await expect(verse).toHaveAttribute('data-count', '4');
  await expect(chorus).toHaveAttribute('data-count', '1');
  await expect(verse).toHaveText('verse');
  await expect(verse).toHaveAttribute('title', /verse · 2 bars · 4 notes/);
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('verse');
});

test('the chosen tab is filled with the colour of its pattern and the rows carry the colour of their channel', async ({ page }) => {
  const on = page.locator('#sq-patterns .sqtab.on');
  const fill = await on.evaluate((el) => getComputedStyle(el).backgroundColor);
  const other = await page.locator('#sq-patterns .sqtab:not(.on)').evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(fill).not.toBe(other);
  const stripes = await page.locator('#sq-patterns .sqgridrow').evaluateAll((rows) => rows.map((r) => getComputedStyle(r).borderLeftColor));
  expect(stripes).toHaveLength(2);
  for (const colour of stripes) expect(colour).not.toBe('rgba(0, 0, 0, 0)');
  expect(stripes[0]).not.toBe(stripes[1]);   // two channels, two colours
});
