import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// The tabs of the patterns: a double-click renames, a right-click has the actions, a drag puts a tab in another place or onto a track of the playlist.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96\npattern chorus bars=1\n  lead 1:0:1:72:96\npattern outro bars=1\n  lead 1:0:1:55:96\n';
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
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(3);
});
const tabNames = (page) => page.locator('#sq-patterns .sqtab').evaluateAll((tabs) => tabs.map((t) => (t as HTMLElement).dataset.pattern));

test('a double-click on the chosen tab asks for a new name', async ({ page }) => {
  await page.locator('#sq-patterns .sqtab.on').dblclick();
  const input = page.locator('dialog[open] input');
  await expect(input).toHaveValue('verse');
  await input.fill('couplet');
  await page.locator('dialog[open] button', { hasText: 'OK' }).click();
  await expect(page.locator('#sq-patterns .sqtab.on')).toHaveText('couplet');
});

test('a right-click on a tab has the actions of the buttons beside the tabs', async ({ page }) => {
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').click({ button: 'right' });
  await expect(page.locator('#menu')).toContainText('Rename...');
  await expect(page.locator('#menu')).toContainText('Duplicate');
  await page.locator('#menu').getByText('Duplicate', { exact: true }).click();
  await expect(page.locator('#sq-patterns .sqtab')).toHaveCount(4);
});

test('a tab dragged onto another tab takes its place', async ({ page }) => {
  expect(await tabNames(page)).toEqual(['verse', 'chorus', 'outro']);
  await page.locator('#sq-patterns .sqtab[data-pattern="outro"]').dragTo(page.locator('#sq-patterns .sqtab[data-pattern="verse"]'));
  await expect.poll(() => tabNames(page)).toEqual(['outro', 'verse', 'chorus']);
});

test('a tab dragged onto a track of the playlist puts a clip of that pattern there', async ({ page }) => {
  const before = await page.locator('#sq .sqclip').count();
  await page.locator('#sq-patterns .sqtab[data-pattern="chorus"]').dragTo(page.locator('#sq .sqtrack').first(), { targetPosition: { x: 200, y: 10 } });
  await expect(page.locator('#sq .sqclip')).toHaveCount(before + 1);
});
