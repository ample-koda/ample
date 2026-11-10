import { test, expect } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// On a desktop the channel rack of the sequencer is tight: the name and the type of a channel share a line, and the fields and buttons are not taller than they need to be.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\nchannel pad notes wave=triangle\npattern verse bars=2\n  lead 1:0:1:60:96\n';
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

test('the name and the type of a channel are on one line', async ({ page }) => {
  const sameLine = await page.locator('#sq-rack .sqchannel[data-channel="lead"]').evaluate((card) => {
    const name = card.querySelector('.sqname')!.getBoundingClientRect(), type = card.querySelector('select')!.getBoundingClientRect();
    return Math.abs((name.top + name.bottom) / 2 - (type.top + type.bottom) / 2) < 6;
  });
  expect(sameLine).toBe(true);
});

test('the fields and the buttons of a channel are low, and the cards are not tall', async ({ page }) => {
  const sizes = await page.locator('#sq-rack .sqchannel[data-channel="lead"]').evaluate((card) => ({
    field: card.querySelector('.seqn')!.getBoundingClientRect().height,
    toggle: card.querySelector('.sqtoggle')!.getBoundingClientRect().height,
    card: card.getBoundingClientRect().height,
  }));
  expect(sizes.field).toBeLessThanOrEqual(26);
  expect(sizes.toggle).toBeLessThanOrEqual(26);
  expect(sizes.card).toBeLessThan(130);   // three lines of 24 px and their gaps
});
