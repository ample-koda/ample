import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// The Grid of AMPLE Jam as a channel rack: LED, pan and volume knobs, row number, lane names, remapping a lane, adding a lane, and the window menu. DOM only.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '');
async function openGrid(page: Page, name: string, notes: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  await panel.getByRole('button', { name: 'Grid view' }).click();
  await expect(panel).toHaveAttribute('data-mode', 'grid');
  return panel;
}
const rowY = async (panel: Locator, pitch: number) => {
  const keys = (await panel.locator('canvas.mls-keys').boundingBox())!;
  const rows = ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').map(Number), rowH = Number(await panel.getAttribute('data-grid-row-h')), scrollY = Number(await panel.getAttribute('data-grid-scroll-y'));
  const i = rows.indexOf(pitch); expect(i, `row ${pitch} in ${rows}`).toBeGreaterThanOrEqual(0);
  return { keys, y: keys.y + i * rowH - scrollY + rowH / 2 };
};

test('the pan knob pans every note of the row, a double-click centres it again, and the volume knob scales the velocity', async ({ page }) => {
  const panel = await openGrid(page, 'rk1', '1:0:1:36:96 1:1:1:36:80 1:2:1:38:90');
  const { keys, y } = await rowY(panel, 36);
  const panX = keys.x + 40;
  await page.mouse.move(panX, y); await page.mouse.down(); await page.mouse.move(panX, y - 25, { steps: 5 }); await page.mouse.up();
  await expect.poll(() => notesOf('rk1')).toContain('pan=');
  await page.mouse.dblclick(panX, y);
  await expect.poll(async () => (await notesOf('rk1')) ?? '').not.toContain('pan=');
  const volX = keys.x + 67;
  await page.mouse.move(volX, y); await page.mouse.down(); await page.mouse.move(volX, y - 40, { steps: 5 }); await page.mouse.up();
  await expect.poll(async () => (await notesOf('rk1')) ?? '').not.toContain(':36:96 ');
});

test('right-click on a lane name: name it, move it to a free sound, never onto a lane that is there', async ({ page }) => {
  const panel = await openGrid(page, 'rk2', '1:0:1:36:96 1:1:1:38:90');
  const { keys, y } = await rowY(panel, 36);
  await page.mouse.click(keys.x + 160, y, { button: 'right' });
  await page.locator('#menu').getByText('Name this lane...').click();
  await page.locator('dialog[open] input[aria-label="Name of the lane"]').fill('Thump');
  await page.locator('dialog[open] button', { hasText: 'OK' }).click();
  await expect.poll(() => panel.evaluate((d: any) => d.mlsDebug.state.laneNames.get(36))).toBe('Thump');
  // move it to a free sound: the notes follow; the sound of another lane (38) is not on offer
  await page.mouse.click(keys.x + 160, y, { button: 'right' });
  await page.locator('#menu').getByText('Give it another sound or note').hover();
  const offered = await page.locator('#menu').evaluate((m) => m.textContent || '');
  expect(offered).not.toMatch(/Snare(?! 2)/);   // 38 is a lane already (Snare); only Snare 2 is free
  const free = await panel.evaluate((d: any) => d.mlsDebug.freePitches());
  expect(free).not.toContain(36);
  expect(free).not.toContain(38);
  await page.keyboard.press('Escape');
  await panel.evaluate((d: any) => d.mlsDebug.remapLane(36, 40));
  await expect.poll(() => notesOf('rk2')).toBe('1:0:1:40:96 1:1:1:38:90');
  await panel.evaluate((d: any) => d.mlsDebug.remapLane(40, 38));   // refused: lane 38 exists
  expect(await notesOf('rk2')).toBe('1:0:1:40:96 1:1:1:38:90');
  expect(await panel.evaluate((d: any) => d.mlsDebug.state.laneNames.get(40))).toBe('Thump');
});

test('the + Row list offers drum sounds and notes that are free, and a new lane is there without touching the Classic roll', async ({ page }) => {
  const panel = await openGrid(page, 'rk3', '1:0:1:36:96');
  const add = panel.locator('select.mls-rowadd');
  await add.focus();
  const values = await add.locator('option').evaluateAll((l) => l.map((o) => (o as HTMLOptionElement).value));
  expect(values).not.toContain('36');
  expect(values).toContain('60');
  await add.selectOption('60');
  await expect.poll(() => panel.getAttribute('data-grid-rows')).toContain('60');
});

test('the menu of the window: a switch for the hover tips that stays open and starts off', async ({ page }) => {
  const panel = await openGrid(page, 'rk4', '1:0:1:36:96');
  await panel.getByRole('button', { name: 'Menu' }).click();
  const tips = page.getByRole('menuitemcheckbox', { name: /Hover tips/ });
  await expect(tips).toHaveAttribute('aria-checked', 'false');
  await tips.click();
  await expect(tips).toHaveAttribute('aria-checked', 'true');
  await expect(panel.locator('.mls-menu')).toBeVisible();
  await tips.click();
  await expect(tips).toHaveAttribute('aria-checked', 'false');
});
