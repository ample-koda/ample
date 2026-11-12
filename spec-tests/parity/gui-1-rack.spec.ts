import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 1: on a desktop the card of a channel and its row of the chosen pattern are on one line (one grid), like the channel rack of FL Studio.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\nchannel pad notes wave=triangle\npattern verse bars=2\n  lead 1:0:1:60:96 1:1:1:64:96\n  bass 1:0:2:36:90\n  pad 1:0:4:48:70\n';
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
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(3);
});

test('the card of every channel and its row of the pattern share one line', async ({ page }) => {
  const lines = await page.evaluate(() => ['lead', 'bass', 'pad'].map((name) => {
    const card = document.querySelector(`#sq-rack .sqchannel[data-channel="${name}"]`)!.getBoundingClientRect();
    const row = document.querySelector(`#sq-patterns .sqgridrow[data-voice="${name}"]`)!.getBoundingClientRect();
    return { name, cardTop: card.top, cardBottom: card.bottom, rowMid: (row.top + row.bottom) / 2, rowLeft: row.left, cardRight: card.right };
  }));
  for (const line of lines) {
    expect(line.rowMid, line.name).toBeGreaterThanOrEqual(line.cardTop - 1);
    expect(line.rowMid, line.name).toBeLessThanOrEqual(line.cardBottom + 1);
    expect(line.rowLeft, line.name).toBeGreaterThan(line.cardRight - 1);   // the row is to the right of the card
  }
  expect(lines[1].cardTop).toBeGreaterThan(lines[0].cardBottom - 2);   // the next channel is below the first
});

test('the pattern tabs and the line of the pattern are above the rows, the playlist is below them', async ({ page }) => {
  const order = await page.evaluate(() => {
    const top = (selector: string) => document.querySelector(selector)!.getBoundingClientRect().top;
    return { tabs: top('#sq-patterns .sqtabs'), head: top('#sq-pattern-head'), firstRow: top('#sq-patterns .sqgridrow'), playlist: top('#sq-playlist') };
  });
  expect(order.tabs).toBeLessThan(order.head);
  expect(order.head).toBeLessThan(order.firstRow);
  expect(order.firstRow).toBeLessThan(order.playlist);
});

test('a channel added from the rack gets its own line at once', async ({ page }) => {
  await page.locator('#sq-add-channel').selectOption('notes');
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(4);
  const aligned = await page.evaluate(() => {
    const cards = [...document.querySelectorAll('#sq-rack .sqchannel')] as HTMLElement[];
    const rows = [...document.querySelectorAll('#sq-patterns .sqgridrow')] as HTMLElement[];
    return cards.length === rows.length && cards.every((card, i) => { const c = card.getBoundingClientRect(), r = rows[i].getBoundingClientRect(); return (r.top + r.bottom) / 2 >= c.top - 1 && (r.top + r.bottom) / 2 <= c.bottom + 1; });
  });
  expect(aligned).toBe(true);
});
