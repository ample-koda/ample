import { test, expect, type Locator, type Page } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// What the classic roll could do and AMPLE Jam now does (plan/jam-replaces-classic): the lower lane, the folded menu with its tools, the scale, the cards and the lanes.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const param = async (block: string, name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === name)?.value;
const notesOf = (block: string) => param(block, 'notes');

async function openJam(page: Page, name: string, notes: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  return panel;
}
async function menuItem(page: Page, panel: Locator, section: string | null, name: RegExp | string, role: 'menuitem' | 'menuitemcheckbox' = 'menuitem') {
  await panel.getByRole('button', { name: 'Menu' }).click();
  if (section) { const head = page.getByRole('button', { name: section }); if ((await head.getAttribute('aria-expanded')) !== 'true') await head.click(); }   // a section stays open the next time the menu opens
  await page.getByRole(role, { name }).click();
}
const selectAll = async (panel: Locator, page: Page) => { await panel.locator('canvas.mls-grid').focus(); await page.keyboard.press('Control+a'); await expect.poll(() => panel.getAttribute('data-selected')).not.toBe(''); };

test('the lower lane switches to pan, a drag sets the note pan and a double-click resets it', async ({ page }) => {
  const panel = await openJam(page, 'jl1', '1:0:0.5:60:96 1:0.5:0.5:64:96');
  await panel.locator('select.mls-lane-select').selectOption('pan');
  const strip = panel.locator('canvas.mls-velocity'), box = (await strip.boundingBox())!;
  const beat = Number(await panel.getAttribute('data-px-beat'));
  await page.mouse.move(box.x + 2, box.y + 8); await page.mouse.down(); await page.mouse.up();
  await expect.poll(async () => (await notesOf('jl1')) as string).toMatch(/^"1:0:0.5:60:96;pan=[1-9][0-9]* /);
  await page.mouse.dblclick(box.x + 2, box.y + 8);
  await expect.poll(async () => (await notesOf('jl1')) as string).not.toContain('pan=');
  expect(beat).toBeGreaterThan(0);
});

test('Reverse in time from the folded menu swaps the order of the selected notes in one edit', async ({ page }) => {
  const panel = await openJam(page, 'jt1', '1:0:0.5:60:96 1:0.5:0.5:64:96');
  await selectAll(panel, page);
  await menuItem(page, panel, 'On the selected notes', /Reverse in time/);
  await expect.poll(async () => (await notesOf('jt1')) as string).toBe('"1:0.5:0.5:60:96 1:0:0.5:64:96"');
});

test('the scale is chosen in the menu and written to the clip', async ({ page }) => {
  const panel = await openJam(page, 'js1', '1:0:0.5:60:96');
  await panel.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('button', { name: 'Scale', exact: true }).click();
  await page.getByLabel('Scale root').selectOption({ label: 'D' });
  await expect.poll(async () => (await param('js1', 'scale')) as string).toMatch(/^"d:/);
});

test('the Properties card sets the colour group of the selected notes', async ({ page }) => {
  const panel = await openJam(page, 'jg1', '1:0:0.5:60:96 1:0.5:0.5:64:96');
  await selectAll(panel, page);
  await menuItem(page, panel, 'Colour groups and properties', /Properties of the selected notes/);
  await panel.getByLabel('Colour group (0 = none)').fill('2');
  await panel.getByRole('button', { name: 'Apply' }).click();
  await expect.poll(async () => (await notesOf('jg1')) as string).toBe('"1:0:0.5:60:96;grp=2 1:0.5:0.5:64:96;grp=2"');
});

test('the riff machine adds notes from a card, and Esc closes the card without changing anything', async ({ page }) => {
  const panel = await openJam(page, 'jr1', '1:0:0.5:60:96');
  await menuItem(page, panel, 'Create', /Riff machine/);
  await expect(panel.locator('.mls-form')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(panel.locator('.mls-form')).toBeHidden();
  expect(await notesOf('jr1')).toBe('"1:0:0.5:60:96"');
  await menuItem(page, panel, 'Create', /Riff machine/);
  await panel.getByLabel('Density %').fill('100');
  await panel.getByRole('button', { name: 'Generate' }).click();
  await expect.poll(async () => ((await notesOf('jr1')) as string).trim().split(' ').length).toBeGreaterThan(3);
});

test('stamping a chord plots its notes where you click, and Esc stops stamping', async ({ page }) => {
  const panel = await openJam(page, 'jc1', '1:0:0.5:60:96');
  await menuItem(page, panel, 'Create', /Stamp a chord/);
  await panel.getByRole('button', { name: 'Start stamping' }).click();
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  await page.mouse.click(box.x + 400, box.y + 120);
  await expect.poll(async () => ((await notesOf('jc1')) as string).trim().split(' ').length).toBe(4);
  await page.keyboard.press('Escape');
  await page.mouse.click(box.x + 600, box.y + 120);
  await expect.poll(async () => ((await notesOf('jc1')) as string).trim().split(' ').length).toBe(5);
});

test('the lanes under the roll are switched on from the menu and show the arrangement', async ({ page }) => {
  const panel = await openJam(page, 'jn1', '1:0:0.5:60:96');
  await expect(panel.locator('.mls-lanes')).toBeHidden();
  await menuItem(page, panel, 'Lanes and clips behind', /Arrangement, sound and automation lanes/, 'menuitemcheckbox');
  await expect(panel.locator('.mls-lanes')).toBeVisible();
  await expect(panel.locator('canvas.mls-lane')).toHaveCount(3);
  // the lanes never get a horizontal scroll bar: the canvases and the key column fit the width of the panel
  expect(await panel.locator('.mls-lanes').evaluate((e: HTMLElement) => e.scrollWidth <= e.clientWidth)).toBe(true);
});
