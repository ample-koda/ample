import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The bars and the colour of a pattern sit in its line, and the explanation of playing and looping is behind an (i) button.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96\npattern chorus bars=1\n  lead 1:0:1:72:96\n';
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
  await expect(page.locator('#sq')).toBeVisible();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(2);
});

test('the bars and the colour of the pattern are in its line, and a change of the bars is kept', async ({ page }) => {
  const head = page.locator('#sq-pattern-head');
  await expect(head.locator('#sq-pattern-bars')).toHaveValue('2');
  await expect(head.getByLabel('Colour of the pattern')).toBeVisible();
  await head.locator('#sq-pattern-bars').fill('3');
  await head.locator('#sq-pattern-bars').press('Tab');
  await expect(page.locator('#sq-pattern-summary')).toHaveText('3 bars · 2 notes');
});

test('the line of a variation says what it is made from', async ({ page }) => {
  await page.locator('#sq-pattern-variation').click();
  await expect(page.locator('#sq-pattern-head #sq-from')).toHaveText('variation of verse');
});

test('the explanation of playing is hidden until the (i) button is pressed, and stays as it was after a change', async ({ page }) => {
  const hint = page.locator('#sq-hint'), toggle = page.locator('#sq-hint-toggle');
  await expect(hint).toBeHidden();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.click();
  await expect(hint).toBeVisible();
  await expect(hint).toContainText('The whole song repeats');
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click();   // a redraw of the workspace keeps it open
  await expect(hint).toBeVisible();
  await page.locator('#sq-hint-toggle').click();
  await expect(hint).toBeHidden();
});
