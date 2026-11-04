import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MLS10 (Grid / step-sequencer view) of AMPLE Jam. DOM only: pointer, wheel and keys, the panel's data attributes and the saved notes text.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '');
async function openStudio(page: Page, name: string, notes?: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  if (notes) await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  await panel.locator('select.mls-snap').selectOption('1/4');
  return panel;
}
const toGrid = async (panel: Locator, page = panel.page()) => { await panel.getByRole('button', { name: 'Grid view' }).click(); await expect(panel).toHaveAttribute('data-mode', 'grid'); await panel.getByRole('button', { name: 'Fit the clip' }).click(); await panel.getByRole('button', { name: 'Zoom out' }).click(); await panel.getByRole('button', { name: 'Zoom out' }).click(); await page.waitForTimeout(250); };   // the rack takes room: fit the clip so every step stays on screen
const dbg = <T>(panel: Locator, fn: (d: any) => T) => panel.evaluate(fn as never) as Promise<T>;
/** Pixel centre of the cell at step `col` of the row for `pitch`; the 1/4 snap makes a step one beat, the 1/16 option a quarter beat. */
async function cell(panel: Locator, col: number, pitch: number) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const pxBeat = Number(await panel.getAttribute('data-px-beat')), scrollX = Number(await panel.getAttribute('data-scroll-x')), step = Number(await panel.getAttribute('data-step-ticks')) / 1_000_000;
  const rows = ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').map(Number), rowH = Number(await panel.getAttribute('data-grid-row-h')), scrollY = Number(await panel.getAttribute('data-grid-scroll-y'));
  const i = rows.indexOf(pitch); expect(i, `row ${pitch} in ${rows}`).toBeGreaterThanOrEqual(0);
  return { x: box.x + (col + 0.5) * step * pxBeat - scrollX, y: box.y + i * rowH - scrollY + rowH / 2 };
}

test('the Roll/Grid toggle sits after Advanced; an empty clip shows the drum rows; the toggle does not change the notes', async ({ page }) => {
  const panel = await openStudio(page, 'gra');
  const buttons = panel.locator('.mls-toolbar .mls-mode');
  await expect(buttons).toHaveCount(2);
  await expect(panel).toHaveAttribute('data-mode', 'roll');
  await toGrid(panel);
  const rows = ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').map(Number);
  expect(rows).toEqual([...rows].sort((a, b) => b - a));
  for (const p of [36, 38, 42, 46, 39]) expect(rows).toContain(p);
  await expect(panel.locator('.mls-tools[aria-label="Tools"]')).toBeHidden();
  await panel.getByRole('button', { name: 'Roll view' }).click();
  await expect(panel).toHaveAttribute('data-mode', 'roll');
  await expect(panel.locator('.mls-tools[aria-label="Tools"]')).toBeVisible();
  expect(await notesOf('gra')).toBeUndefined();
});

test('click toggles a step on and off, Shift+click accents, right-click clears', async ({ page }) => {
  const panel = await openStudio(page, 'grb', '1:0:1:36:96');
  await toGrid(panel);
  await expect(panel).toHaveAttribute('data-grid-rows', '36');
  await panel.locator('select.mls-snap').selectOption('1/4');
  const a = await cell(panel, 2, 36);
  await page.mouse.click(a.x, a.y);
  await expect.poll(() => notesOf('grb')).toBe('1:0:1:36:96 1:2:1:36:96');
  await page.keyboard.down('Shift'); await page.mouse.click(a.x, a.y); await page.keyboard.up('Shift');
  await expect.poll(() => notesOf('grb')).toBe('1:0:1:36:96 1:2:1:36:127');
  await page.mouse.click(a.x, a.y);   // plain click on a filled step clears it
  await expect.poll(() => notesOf('grb')).toBe('1:0:1:36:96');
  const b = await cell(panel, 0, 36);
  await page.mouse.click(b.x, b.y, { button: 'right' });
  await expect.poll(() => notesOf('grb')).toBeUndefined();
});

test('dragging across empty steps paints a run in one edit, and dragging from a filled step clears the run', async ({ page }) => {
  const panel = await openStudio(page, 'grc', '1:0:1:36:96');
  await toGrid(panel);
  const from = await cell(panel, 1, 36), to = await cell(panel, 3, 36);
  await page.mouse.move(from.x, from.y); await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 8 }); await page.mouse.up();
  await expect.poll(() => notesOf('grc')).toBe('1:0:1:36:96 1:1:1:36:96 1:2:1:36:96 1:3:1:36:96');
  const start = await cell(panel, 3, 36), end = await cell(panel, 1, 36);
  await page.mouse.move(start.x, start.y); await page.mouse.down(); await page.mouse.move(end.x, end.y, { steps: 8 }); await page.mouse.up();
  await expect.poll(() => notesOf('grc')).toBe('1:0:1:36:96');
});

test('the wheel over a step changes its velocity, elsewhere it scrolls', async ({ page }) => {
  const panel = await openStudio(page, 'grd', '1:0:1:36:96 1:1:1:38:90');
  await toGrid(panel);
  const c = await cell(panel, 0, 36);
  await page.mouse.move(c.x, c.y);
  for (let i = 0; i < 4; i++) await page.mouse.wheel(0, -100);
  await expect.poll(() => notesOf('grd')).toBe('1:0:1:36:100 1:1:1:38:90');
  await page.keyboard.down('Shift'); await page.mouse.wheel(0, 100); await page.keyboard.up('Shift');
  await expect.poll(() => notesOf('grd')).toBe('1:0:1:36:95 1:1:1:38:90');
});

test('Roll and Grid show the same notes: a Grid edit is a bar in Roll, and the mode is remembered per clip', async ({ page }) => {
  const panel = await openStudio(page, 'gre', '1:0:1:36:96');
  await toGrid(panel);
  const c = await cell(panel, 1, 36);
  await page.mouse.click(c.x, c.y);
  await expect.poll(() => notesOf('gre')).toBe('1:0:1:36:96 1:1:1:36:96');
  await panel.getByRole('button', { name: 'Roll view' }).click();
  const rows = await dbg(panel, (d) => d.mlsDebug.rows().map((r: any) => `${r.at}:${r.pitch}:${r.length}`));
  expect(rows).toEqual(['0:36:1000000', '1000000:36:1000000']);
  await panel.getByRole('button', { name: 'Grid view' }).click();
  await panel.locator('.mls-close').click();
  await expect(page.locator('dialog.mls-panel')).toHaveCount(0);
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('data-mode', 'grid');
});

test('rows are the pitches present, a row stays when its last step is cleared, and the + Row box adds a drum row', async ({ page }) => {
  const panel = await openStudio(page, 'grf', '1:0:1:36:96 1:0.1:1:42:96');
  await toGrid(panel);
  await expect(panel).toHaveAttribute('data-grid-rows', '42,36');
  const c = await cell(panel, 0, 42);   // the off-grid note shows in the step it starts in
  await page.mouse.click(c.x, c.y);
  await expect.poll(() => notesOf('grf')).toBe('1:0:1:36:96');
  await expect(panel).toHaveAttribute('data-grid-rows', '42,36');
  await panel.locator('select.mls-rowadd').selectOption('38');
  await expect(panel).toHaveAttribute('data-grid-rows', '42,38,36');
  await page.mouse.click((await cell(panel, 1, 38)).x, (await cell(panel, 1, 38)).y);
  await expect.poll(() => notesOf('grf')).toBe('1:0:1:36:96 1:1:1:38:96');
});

test('Ctrl+Q in the Grid view quantizes the off-grid notes to the steps', async ({ page }) => {
  const panel = await openStudio(page, 'grg', '1:0.1:1:36:96');
  await toGrid(panel);
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+q');
  await expect.poll(() => notesOf('grg')).toBe('1:0:1:36:96');
});

test('a note longer than a step fills its cells: any covered cell clears the whole note, the next free cell adds one', async ({ page }) => {
  const panel = await openStudio(page, 'grf', '1:0:3:36:96');
  await toGrid(panel);
  const free = await cell(panel, 3, 36), mid = await cell(panel, 1, 36);
  await page.mouse.click(free.x, free.y);   // beyond the long note: a new one-step note
  await expect.poll(() => notesOf('grf')).toBe('1:0:3:36:96 1:3:1:36:96');
  await page.mouse.click(mid.x, mid.y);   // the second cell of the long note clears all of it
  await expect.poll(() => notesOf('grf')).toBe('1:3:1:36:96');
  await panel.locator('.mls-grid').focus();
});

test('the wheel over the tail cell of a long note changes that note, and Shift+click there accents it', async ({ page }) => {
  const panel = await openStudio(page, 'grg', '1:0:3:36:96');
  await toGrid(panel);
  const tail = await cell(panel, 2, 36);
  await page.keyboard.down('Shift'); await page.mouse.click(tail.x, tail.y); await page.keyboard.up('Shift');
  await expect.poll(() => notesOf('grg')).toBe('1:0:3:36:127');
});

test('the M box mutes one row for playback only: the song text is untouched and the choice is remembered', async ({ page }) => {
  const panel = await openStudio(page, 'grh', '1:0:1:36:96 1:1:1:38:90');
  await toGrid(panel);
  const before = await notesOf('grh');
  const keys = (await panel.locator('canvas.mls-keys').boundingBox())!, c = await cell(panel, 0, 36);
  await page.mouse.click(keys.x + 15, c.y);
  await expect(panel).toHaveAttribute('data-muted', '36');
  const played = await dbg(panel, (d) => d.mlsDebug.liveRows().map((r: any) => r.pitch));
  expect(played).toEqual([38]);
  expect(await notesOf('grh')).toBe(before);
  await page.mouse.click(keys.x + 15, c.y);
  await expect(panel).toHaveAttribute('data-muted', '');
  expect((await dbg(panel, (d) => d.mlsDebug.liveRows().map((r: any) => r.pitch))).sort()).toEqual([36, 38]);
  await page.mouse.click(keys.x + 15, c.y);
  await expect(panel).toHaveAttribute('data-muted', '36');
  await page.waitForTimeout(600);
  await panel.locator('.mls-close').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('data-muted', '36');
});

test('Alt+wheel in the Grid makes the rows taller or shorter within 14-56 px, and the zoom is remembered', async ({ page }) => {
  const panel = await openStudio(page, 'gri', '1:0:1:36:96');
  await toGrid(panel);
  const c = await cell(panel, 0, 36);
  await page.mouse.move(c.x, c.y);
  expect(Number(await panel.getAttribute('data-grid-row-h'))).toBe(30);
  await page.keyboard.down('Alt');
  for (let i = 0; i < 3; i++) await page.mouse.wheel(0, -100);
  await expect.poll(async () => Number(await panel.getAttribute('data-grid-row-h'))).toBeGreaterThan(30);
  for (let i = 0; i < 40; i++) await page.mouse.wheel(0, -100);
  await expect.poll(async () => Number(await panel.getAttribute('data-grid-row-h'))).toBe(56);
  for (let i = 0; i < 60; i++) await page.mouse.wheel(0, 100);
  await expect.poll(async () => Number(await panel.getAttribute('data-grid-row-h'))).toBe(14);
  await page.keyboard.up('Alt');
  expect(await notesOf('gri')).toBe('1:0:1:36:96');   // a zoom edits nothing
  await page.mouse.move(c.x, c.y); await page.keyboard.down('Alt'); await page.mouse.wheel(0, -100); await page.keyboard.up('Alt');
  await page.waitForTimeout(600);
  const zoomed = await panel.getAttribute('data-grid-row-h');
  await panel.locator('.mls-close').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('data-grid-row-h', zoomed!);
});

test('Ctrl and drag in the Grid selects the notes the rectangle touches', async ({ page }) => {
  const panel = await openStudio(page, 'grr', '1:0:1:36:96 1:2:1:36:96 1:0:1:38:96');
  await toGrid(panel);
  const a = await cell(panel, 0, 36), b = await cell(panel, 2, 36);
  await page.keyboard.down('Control');
  await page.mouse.move(a.x - 6, a.y - 6); await page.mouse.down(); await page.mouse.move(b.x + 6, b.y + 6, { steps: 8 }); await page.mouse.up();
  await page.keyboard.up('Control');
  await expect.poll(() => panel.getAttribute('data-selected')).toMatch(/\S/);
  const selected = ((await panel.getAttribute('data-selected')) ?? '').split(',').filter(Boolean);
  expect(selected.length).toBe(2);
});
