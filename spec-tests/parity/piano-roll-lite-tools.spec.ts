import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// Remaining PARITY.md rows of AMPLE Jam: the MIDI clipboard (Shift+Ctrl+V), Ctrl+Alt+Q, the Playback (Y) and Zoom (Z) tools and monophonic Paint.
// DOM only: pointer, keys, the panel's data attributes and the saved notes text. How the tools feel next to FL stays owner-observed.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await page.context().grantPermissions(['clipboard-read', 'clipboard-write']); await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block).parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '');
const versionOf = async () => (await api('/api/doc?path=song.amplet')).version as string;
async function openLite(page: Page, name: string, notes?: string) {
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
async function at(panel: Locator, beat: number, pitch: number, dx = 0.35) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const v = await panel.evaluate((d: HTMLElement & { mlsDebug: { state: { pxBeat: number; rowH: number; scrollX: number; scrollY: number } } }) => d.mlsDebug.state);
  return { x: box.x + (beat + dx) * v.pxBeat - v.scrollX, y: box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2 };
}
const view = (panel: Locator) => panel.evaluate((d: HTMLElement & { mlsDebug: { state: { pxBeat: number; rowH: number } } }) => ({ pxBeat: d.mlsDebug.state.pxBeat, rowH: d.mlsDebug.state.rowH }));

test('Shift+Ctrl+V pastes note text from the system clipboard at the pointer, with every field; Ctrl+C writes that text', async ({ page }) => {
  const panel = await openLite(page, 'clpa', '1:0:1:60:96');
  await page.evaluate(() => navigator.clipboard.writeText('1:0:1:67:100;pan=5 1:1:1:71:90'));
  const where = await at(panel, 2, 60, 0.05);   // the clip is one bar: the paste lands where the two beats still fit
  await page.mouse.move(where.x, where.y);
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+Shift+v');
  await expect.poll(() => notesOf('clpa')).toBe('1:0:1:60:96 1:2:1:67:100;pan=5 1:3:1:71:90');
  // copy puts the selected notes on the clipboard as text
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+c');
  await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toContain(';pan=5');
  const text = await page.evaluate(() => navigator.clipboard.readText());
  expect(text.split(' ').length).toBe(3);
});

test('Shift+Ctrl+V with text that holds no notes pastes nothing and writes nothing', async ({ page }) => {
  const panel = await openLite(page, 'clpb', '1:0:1:60:96');
  const before = await versionOf();
  await page.evaluate(() => navigator.clipboard.writeText('hello, not notes'));
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+Shift+v');
  await page.waitForTimeout(400);
  expect(await versionOf()).toBe(before);
  expect(await notesOf('clpb')).toBe('1:0:1:60:96');
});

test('Ctrl+Alt+Q quick-quantizes like Ctrl+Q', async ({ page }) => {
  const panel = await openLite(page, 'qka', '1:0.37:0.5:60:96 1:2.4:0.5:64:90');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+Alt+q');
  await expect.poll(() => notesOf('qka')).toBe('1:0:0.5:60:96 1:2:0.5:64:90');
});

test('the Playback tool (Y) starts playing from the clicked position; right-click stops', async ({ page }) => {
  const panel = await openLite(page, 'pya', '1:0:1:60:96');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('y');
  await expect(panel).toHaveAttribute('data-tool', 'playback');
  const where = await at(panel, 2, 60, 0.05), before = await versionOf();
  await page.mouse.click(where.x, where.y);
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'true', { timeout: 10_000 });
  await expect.poll(async () => Number(await panel.getAttribute('data-playhead'))).toBeGreaterThanOrEqual(1_900_000);
  await page.mouse.click(where.x, where.y, { button: 'right' });
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'false');
  expect(await versionOf()).toBe(before);   // the tool never edits
  expect(await notesOf('pya')).toBe('1:0:1:60:96');
});

test('the Zoom tool (Z): a dragged box fills the view, a click zooms in, right-click zooms out, no edit', async ({ page }) => {
  const panel = await openLite(page, 'zma', '1:0:1:60:96');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('z');
  await expect(panel).toHaveAttribute('data-tool', 'zoom');
  const before = await versionOf(), v0 = await view(panel), c = await at(panel, 1, 62);
  await page.mouse.click(c.x, c.y);
  await expect.poll(async () => (await view(panel)).pxBeat).toBeGreaterThan(v0.pxBeat * 1.5);
  const v1 = await view(panel), c1 = await at(panel, 1, 62);
  await page.mouse.click(c1.x, c1.y, { button: 'right' });
  await expect.poll(async () => (await view(panel)).pxBeat).toBeLessThan(v1.pxBeat * 0.7);
  const a = await at(panel, 0.5, 64, 0), b = await at(panel, 1.5, 58, 0), vBefore = await view(panel);
  await page.mouse.move(a.x, a.y); await page.mouse.down(); await page.mouse.move(b.x, b.y, { steps: 6 }); await page.mouse.up();
  await expect.poll(async () => (await view(panel)).pxBeat).toBeGreaterThan(vBefore.pxBeat * 2);
  expect((await view(panel)).rowH).toBeGreaterThan(vBefore.rowH);
  expect(await versionOf()).toBe(before);
});

test('Paint draws a run; with Shift held (monophonic) each step column keeps one note', async ({ page }) => {
  const panel = await openLite(page, 'pna');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('b');
  await expect(panel).toHaveAttribute('data-tool', 'paint');
  // plain: dragging up through one column paints every row it crosses
  const low = await at(panel, 0, 60), high = await at(panel, 0, 63);
  await page.mouse.move(low.x, low.y); await page.mouse.down(); await page.mouse.move(high.x, high.y, { steps: 8 }); await page.mouse.up();
  await expect.poll(async () => ((await notesOf('pna')) ?? '').split(' ').length).toBe(4);
  // Shift: the same drag in the next column leaves only the last pitch
  const low2 = await at(panel, 2, 60), high2 = await at(panel, 2, 63);
  await page.keyboard.down('Shift');
  await page.mouse.move(low2.x, low2.y); await page.mouse.down(); await page.mouse.move(high2.x, high2.y, { steps: 8 }); await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect.poll(async () => ((await notesOf('pna')) ?? '').split(' ').filter((t) => t.startsWith('1:2:')).length).toBe(1);
  const mono = ((await notesOf('pna')) ?? '').split(' ').filter((t) => t.startsWith('1:2:'));
  expect(mono[0].split(':')[3]).toBe('63');
});
