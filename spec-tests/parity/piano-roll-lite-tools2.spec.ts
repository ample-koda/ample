import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// AMPLE Jam: split and join, drum-kit notes, scrollbars, hover tips, the window menu, the shortcuts card, the end line and the row height. DOM only.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '');
async function openJam(page: Page, name: string, notes: string, grid = false) {
  await addTemplateBlock(page, 'midi-clip', name);
  await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  await panel.locator('select.mls-snap').selectOption('1/4');
  if (grid) {
    await panel.getByRole('button', { name: 'Grid view' }).click();
    await expect(panel).toHaveAttribute('data-mode', 'grid');
    await panel.getByRole('button', { name: 'Fit the clip' }).click();
    await panel.getByRole('button', { name: 'Zoom out' }).click(); await panel.getByRole('button', { name: 'Zoom out' }).click();
    await page.waitForTimeout(250);
  }
  return panel;
}
async function cell(panel: Locator, col: number, pitch: number) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const pxBeat = Number(await panel.getAttribute('data-px-beat')), scrollX = Number(await panel.getAttribute('data-scroll-x')), step = Number(await panel.getAttribute('data-step-ticks')) / 1_000_000;
  const rows = ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').map(Number), rowH = Number(await panel.getAttribute('data-grid-row-h')), scrollY = Number(await panel.getAttribute('data-grid-scroll-y'));
  const i = rows.indexOf(pitch); expect(i, `row ${pitch} in ${rows}`).toBeGreaterThanOrEqual(0);
  return { x: box.x + (col + 0.5) * step * pxBeat - scrollX, y: box.y + i * rowH - scrollY + rowH / 2 };
}

test('J joins the selected neighbours into long notes, S splits long notes into steps', async ({ page }) => {
  const panel = await openJam(page, 'tk1', '1:0:1:36:96 1:1:1:36:96 1:2:1:38:90 1:3:1:38:90', true);
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('j');
  await expect.poll(() => notesOf('tk1')).toBe('1:0:2:36:96 1:2:2:38:90');   // two pairs: two long notes
  await page.keyboard.press('Control+a');
  await page.keyboard.press('s');
  await expect.poll(() => notesOf('tk1')).toBe('1:0:1:36:96 1:1:1:36:96 1:2:1:38:90 1:3:1:38:90');
});

test('Alt+drag draws one long note over the steps and Alt+click splits it', async ({ page }) => {
  const panel = await openJam(page, 'tk2', '1:3:1:36:96', true);
  const from = await cell(panel, 0, 36), to = await cell(panel, 2, 36);
  await page.keyboard.down('Alt');
  await page.mouse.move(from.x, from.y); await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 6 }); await page.mouse.up();
  await page.keyboard.up('Alt');
  const sorted = async () => ((await notesOf('tk2')) ?? '').split(' ').sort().join(' ');   // a new note is written after the others
  await expect.poll(sorted).toMatch(/^1:0:3:36:\d+ 1:3:1:36:96$/);
  const mid = await cell(panel, 1, 36);
  await page.keyboard.down('Alt'); await page.mouse.click(mid.x, mid.y); await page.keyboard.up('Alt');
  await expect.poll(sorted).toMatch(/^1:0:1:36:\d+ 1:1:1:36:\d+ 1:2:1:36:\d+ 1:3:1:36:96$/);
});

test('K sets the selected notes to the drum kit (channel 10) and back to the instrument', async ({ page }) => {
  const panel = await openJam(page, 'tk3', '1:0:1:36:96 1:1:1:38:90');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('k');
  await expect.poll(async () => (await notesOf('tk3')) ?? '').toBe('1:0:1:36:96:9:0:0 1:1:1:38:90:9:0:0');
  await page.keyboard.press('k');
  await expect.poll(async () => (await notesOf('tk3')) ?? '').toBe('1:0:1:36:96 1:1:1:38:90');
});

test('the scrollbars are there when the clip is wider than the view and show when the pointer is near them', async ({ page }) => {
  const panel = await openJam(page, 'tk4', '20:0:1:60:96');
  const bar = panel.locator('.mls-sb-h');
  await expect(bar).not.toBeHidden();
  await expect(bar).toHaveCSS('opacity', '0');
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height - 6);
  await expect(bar).toHaveCSS('opacity', '1');
  const before = await panel.evaluate((d: any) => d.mlsDebug.state.scrollX);
  const t = (await bar.locator('i').boundingBox())!;
  await page.mouse.move(t.x + t.width / 2, t.y + t.height / 2); await page.mouse.down(); await page.mouse.move(t.x + t.width / 2 + 80, t.y + t.height / 2, { steps: 4 }); await page.mouse.up();
  expect(await panel.evaluate((d: any) => d.mlsDebug.state.scrollX)).toBeGreaterThan(before);
});

test('the hover tips of the editor are off until the menu switches them on; the menu closes when the window is rolled up', async ({ page }) => {
  const panel = await openJam(page, 'tk5', '1:0:1:60:96');
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  await page.mouse.move(box.x + 200, box.y + 60);
  await page.waitForTimeout(700);
  await expect(panel.locator('.mls-tip')).toBeHidden();
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitemcheckbox', { name: /Hover tips/ }).click();
  await expect(panel.locator('.mls-menu')).toBeVisible();
  await panel.locator('.mls-title').dblclick({ position: { x: 150, y: 8 } });
  await expect(panel).toHaveAttribute('data-window', 'collapsed');
  await expect(panel.locator('.mls-menu')).toBeHidden();
});

test('the shortcuts card is grouped and the end line is mint with room, orange when a note touches the end', async ({ page }) => {
  const panel = await openJam(page, 'tk6', '1:0:1:60:96');
  expect(await panel.evaluate((d: any) => d.mlsDebug.endTone())).toBe('#3fd6b0');
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: 'Shortcuts' }).click();
  await expect(panel.locator('.mls-help-group')).toHaveCount(7);
  await expect(panel.locator('.mls-help')).toContainText('Ctrl+Q');
});

test('the end line is orange when a note touches the end of the clip', async ({ page }) => {
  const panel = await openJam(page, 'tk7', '1:3:1:60:96');
  expect(await panel.evaluate((d: any) => d.mlsDebug.endTone())).toBe('#f0a040');
});

test('zooming the time in also makes the rows a little taller, much less than the time', async ({ page }) => {
  const panel = await openJam(page, 'tk8', '1:0:1:60:96');
  const read = () => panel.evaluate((d: any) => ({ px: d.mlsDebug.state.pxBeat as number, row: d.mlsDebug.state.rowH as number }));
  const before = await read();
  await panel.getByRole('button', { name: 'Zoom in' }).click();
  const after = await read();
  expect(after.px).toBeGreaterThan(before.px * 1.2);
  expect(after.row).toBeGreaterThan(before.row);
  expect(after.row).toBeLessThan(before.row * 1.12);
});
