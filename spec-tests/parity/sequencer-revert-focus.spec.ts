import { test, expect, Page } from '@playwright/test';
import { api, freshServer, node, openApp } from '../../web/tests/support';

// Issues #262 and #263: Revert in the Sequencer also works after Play or Build wrote the staged text,
// and a redraw keeps the keyboard on the same control.
const SEQ = 'sequencer 1\nchannel lead notes wave=square\nchannel bass notes wave=saw\n'
  + 'pattern verse bars=2\n  lead 1:0:1:60:96\n  bass 1:0:2:36:90\nplaylist\n  clip verse track=1 at=1 for=2\n';
freshServer({
  song: 'composition "band" { ample 1  tempo 120  master-seed 3 }\n'
    + 'sequencer sq at 1 for 4 {\n  file sequences/a.seq\n  lanes lane1=lead\n}\n',
  files: { 'sequences/a.seq': SEQ },
});

const seqText = async () => (await api('/api/file?path=sequences/a.seq')).text as string;
const menuItem = (page: Page, text: string) => page
  .locator('#menu div', { hasText: text })
  .filter({ hasNot: page.locator('div', { hasText: text }) })
  .first();
const dialogButton = (page: Page, name: string) => page.locator('#dlg-btns button', { hasText: name });

test.beforeEach(async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'sq').click({ position: { x: 20, y: 36 } });
  await page.locator('#open-sequencer').click();
  await expect(page.locator('#sq-patterns .sqgridrow')).toHaveCount(2);
});

test('Revert in the Sequencer restores the original text after Play wrote the staged edit @quarantine', async ({ page }) => {
  const before = await seqText();
  await page.locator('#sq-add-channel').selectOption({ index: 1 });   // a staged edit: one more channel
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(3);
  await page.locator('#sq-play').click();                              // Play writes the file and builds
  await expect.poll(seqText, { timeout: 60000 }).not.toBe(before);
  await page.locator('#sq-stop').click();
  await page.locator('#sq .savectl .savemore').click();
  await menuItem(page, 'Revert changes').click();                      // still offered although the text was written
  await dialogButton(page, 'Revert').click();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(2);
  await expect.poll(seqText).toBe(before);                             // and the file on disk is the original again
});

test('Revert in the Sequencer after a Save goes back to the saved text, not to the editor start', async ({ page }) => {
  await page.locator('#sq-add-channel').selectOption({ index: 1 });
  await page.locator('#sq .savebtn').click();
  await expect.poll(async () => (await seqText()).split('\n').filter((l) => l.startsWith('channel')).length).toBe(3);
  await page.locator('#sq-add-channel').selectOption({ index: 1 });
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(4);
  await page.locator('#sq .savectl .savemore').click();
  await menuItem(page, 'Revert changes').click();
  await dialogButton(page, 'Revert').click();
  await expect(page.locator('#sq-rack .sqchannel')).toHaveCount(3);
});

test('after a number is typed and Enter is pressed the same control keeps the focus @quarantine', async ({ page }) => {
  const field = page.locator('#sq-rack .sqchannel').first().locator('input.seqn').first();
  const title = await field.getAttribute('title');
  await field.focus();
  await field.fill('-6');
  await page.keyboard.press('Enter');
  await expect.poll(() => page.evaluate(() => document.activeElement?.getAttribute('title'))).toBe(title);
  await expect.poll(() => page.evaluate(() => (document.activeElement?.closest('.sqchannel') ? 1 : 0))).toBe(1);
});

test('Tab after typing a number moves on instead of coming back to the redrawn input @quarantine', async ({ page }) => {
  const field = page.locator('#sq-rack .sqchannel').first().locator('input.seqn').first();
  const title = await field.getAttribute('title');
  await field.focus();
  await field.fill('-7');
  await page.keyboard.press('Tab');
  await expect.poll(() => page.evaluate((t) => {
    const a = document.activeElement as HTMLElement | null;
    return !!a && a !== document.body && a.getAttribute('title') !== t;
  }, title)).toBe(true);
});
