import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MLS12 (editing-flow parity) and MLS07 (Advanced <-> Lite switching, Simplify to Lite) of AMPLE Jam. DOM only: pointer, wheel and keys, the recorded
// audition calls and the panel's data attributes; no audio is heard and no screenshot is taken.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const paramOf = async (block: string, name: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === name)?.value as string | undefined)?.replace(/^"|"$/g, '');
const notesOf = (block: string) => paramOf(block, 'notes');
const blockNames = async () => (await api('/api/doc?path=song.amplet')).blocks.map((b: { name: string }) => b.name) as string[];
const versionOf = async () => (await api('/api/doc?path=song.amplet')).version as string;

async function makeClip(page: Page, name: string, notes?: string, extra: [string, string][] = []) {
  await addTemplateBlock(page, 'midi-clip', name);
  const ops: unknown[] = [];
  if (notes) ops.push({ type: 'setParameter', block: name, name: 'notes', value: `"${notes}"` });
  for (const [key, value] of extra) ops.push({ type: 'setParameter', block: name, name: key, value });
  if (ops.length) await page.evaluate(async (list) => { const s = await import('/app/store.js'); await s.edit(list as never); }, ops);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
}
async function openStudio(page: Page, name: string, notes?: string, extra: [string, string][] = [], snap: string | null = '1/4') {
  await makeClip(page, name, notes, extra);
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  return litePanel(page, snap);
}
async function litePanel(page: Page, snap: string | null = '1/4') {
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  if (snap) await panel.locator('select.mls-snap').selectOption(snap);
  return panel;
}
const dbg = <T>(panel: Locator, fn: (d: any) => T) => panel.evaluate(fn as never) as Promise<T>;
const viewOf = (panel: Locator) => dbg(panel, (d) => ({ pxBeat: d.mlsDebug.state.pxBeat as number, rowH: d.mlsDebug.state.rowH as number, scrollX: d.mlsDebug.state.scrollX as number, scrollY: d.mlsDebug.state.scrollY as number }));
async function at(panel: Locator, beat: number, pitch: number, dx = 0.35) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!, v = await viewOf(panel);
  return { x: box.x + (beat + dx) * v.pxBeat - v.scrollX, y: box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2 };
}
const selected = async (panel: Locator) => (await panel.getAttribute('data-selected')) ?? '';
const clickNote = async (page: Page, panel: Locator, beat: number, pitch: number) => { const p = await at(panel, beat, pitch); await page.mouse.click(p.x, p.y); };

test('Alt+wheel over a note sets its velocity in one edit; Ctrl+Alt+wheel fine-tunes it', async ({ page }) => {
  const panel = await openStudio(page, 'wva', '1:0:1:60:96;pan=20 1:2:1:64:90');
  const p = await at(panel, 0, 60);
  await page.mouse.move(p.x, p.y);
  await page.keyboard.down('Alt');
  for (let i = 0; i < 5; i++) await page.mouse.wheel(0, -100);
  await page.keyboard.up('Alt');
  await expect.poll(() => notesOf('wva')).toBe('1:0:1:60:101;pan=20 1:2:1:64:90');
  await page.keyboard.down('Control'); await page.keyboard.down('Alt');
  for (let i = 0; i < 3; i++) await page.mouse.wheel(0, 100);
  await page.keyboard.up('Alt'); await page.keyboard.up('Control');
  await expect.poll(() => notesOf('wva')).toBe('1:0:1:60:101;pan=20;fine=-3 1:2:1:64:90');
});

test('Ctrl+wheel zooms time and Alt+wheel zooms rows with the pointer position kept', async ({ page }) => {
  const panel = await openStudio(page, 'zma', '1:0:1:60:96 2:0:1:64:90');
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!, px = 230, py = 90;
  const before = await viewOf(panel);
  const beat = (px + before.scrollX) / before.pxBeat, row = (py + before.scrollY) / before.rowH;
  await page.mouse.move(box.x + px, box.y + py);
  await page.keyboard.down('Control'); await page.mouse.wheel(0, -100); await page.keyboard.up('Control');
  const t = await viewOf(panel);
  expect(t.pxBeat).toBeGreaterThan(before.pxBeat);
  expect(Math.abs((px + t.scrollX) / t.pxBeat - beat)).toBeLessThan(0.02);
  await page.keyboard.down('Alt'); await page.mouse.wheel(0, -100); await page.keyboard.up('Alt');
  const r = await viewOf(panel);
  expect(r.rowH).toBeGreaterThan(t.rowH);
  expect(Math.abs((py + r.scrollY) / r.rowH - row)).toBeLessThan(0.05);
});

test('PgUp/PgDn zoom and Shift+1..5 set the zoom levels, fit the clip and zoom to the selection', async ({ page }) => {
  const panel = await openStudio(page, 'zka', '1:0:1:60:96 2:0:1:64:90');
  const grid = panel.locator('canvas.mls-grid');
  await grid.focus();
  const base = (await viewOf(panel)).pxBeat;
  await page.keyboard.press('PageUp'); expect((await viewOf(panel)).pxBeat).toBeCloseTo(base * 1.25, 1);
  await page.keyboard.press('PageDown'); await page.keyboard.press('PageDown'); expect((await viewOf(panel)).pxBeat).toBeLessThan(base);
  await page.keyboard.press('Shift+1'); expect((await viewOf(panel)).pxBeat).toBeCloseTo(24, 1);
  await page.keyboard.press('Shift+3'); expect((await viewOf(panel)).pxBeat).toBeCloseTo(140, 1);
  await page.keyboard.press('Shift+4'); const fit = (await viewOf(panel)).pxBeat;
  expect(fit).toBeLessThan(140);
  await clickNote(page, panel, 4, 64);
  await page.keyboard.press('Shift+5');
  expect((await viewOf(panel)).pxBeat).toBeGreaterThan(fit);
});

test('copy and paste keep every field and land at the pointer, else at the playhead', async ({ page }) => {
  const panel = await openStudio(page, 'cpa', '1:0:1:60:96;pan=20;fine=5 1:2:1:64:90');
  await clickNote(page, panel, 0, 60);
  await page.keyboard.press('Control+c');
  const target = await at(panel, 1, 60);
  await page.mouse.move(target.x, target.y);
  await page.keyboard.press('Control+v');
  await expect.poll(() => notesOf('cpa')).toBe('1:0:1:60:96;pan=20;fine=5 1:2:1:64:90 1:1:1:60:96;pan=20;fine=5');
  // pointer off the grid: the playhead (clicked on the ruler at beat 1) decides
  const ruler = (await panel.locator('canvas.mls-ruler').boundingBox())!, v = await viewOf(panel);
  await page.mouse.click(ruler.x + 3 * v.pxBeat - v.scrollX + 2, ruler.y + 12);
  await page.mouse.move(ruler.x + 5, ruler.y - 40);
  await expect(panel).not.toHaveAttribute('data-playhead', '0');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+v');
  await expect.poll(() => notesOf('cpa')).toBe('1:0:1:60:96;pan=20;fine=5 1:2:1:64:90 1:1:1:60:96;pan=20;fine=5 1:3:1:60:96;pan=20;fine=5');
});

test('Ctrl+X cuts, Ctrl+B duplicates to the right, Ctrl+D deselects and Ctrl+Z undoes a paste in one step', async ({ page }) => {
  const panel = await openStudio(page, 'cba', '1:0:1:60:96 1:1:1:64:90');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+b');
  await expect.poll(() => notesOf('cba')).toBe('1:0:1:60:96 1:1:1:64:90 1:2:1:60:96 1:3:1:64:90');
  await expect.poll(() => selected(panel)).toBe('2,3');
  await page.keyboard.press('Control+d');
  await expect.poll(() => selected(panel)).toBe('');
  await clickNote(page, panel, 2, 60);
  await page.keyboard.press('Control+x');
  await expect.poll(() => notesOf('cba')).toBe('1:0:1:60:96 1:1:1:64:90 1:3:1:64:90');
  await page.waitForTimeout(400);
  await page.keyboard.press('Control+z');
  await expect.poll(() => notesOf('cba')).toBe('1:0:1:60:96 1:1:1:64:90 1:2:1:60:96 1:3:1:64:90');
});

test('Ctrl+Q quantizes the selection to the snap and a second Ctrl+Q changes nothing', async ({ page }) => {
  const panel = await openStudio(page, 'qqa', '1:0.2:0.3:60:96;pan=4 1:1.9:0.3:64:90 2:0:0.5:67:80');
  const grid = panel.locator('canvas.mls-grid');
  await grid.focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+q');
  await expect.poll(() => notesOf('qqa')).toBe('1:0:0.3:60:96;pan=4 1:2:0.3:64:90 2:0:0.5:67:80');
  const version = await versionOf();
  await page.keyboard.press('Control+q');
  await page.waitForTimeout(300);
  expect(await versionOf()).toBe(version);
});

test('holding Alt shows free on the snap box and draws off the grid; releasing restores it', async ({ page }) => {
  const panel = await openStudio(page, 'alta', '1:0:1:60:96');
  const snap = panel.locator('select.mls-snap');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.down('Alt');
  await expect(snap).toHaveValue('free');
  await page.keyboard.up('Alt');
  await expect(snap).toHaveValue('1/4');
});

test('Ctrl+drag on the ruler selects the notes that start in the time range', async ({ page }) => {
  const panel = await openStudio(page, 'rsa', '1:0:1:60:96 1:1:1:62:96 2:0:1:64:96 3:0:1:65:96');
  const ruler = (await panel.locator('canvas.mls-ruler').boundingBox())!, v = await viewOf(panel);
  const x = (beat: number) => ruler.x + beat * v.pxBeat - v.scrollX;
  await page.keyboard.down('Control');
  await page.mouse.move(x(0.9), ruler.y + 12); await page.mouse.down();
  await page.mouse.move(x(4.1), ruler.y + 12, { steps: 8 }); await page.mouse.up();
  await page.keyboard.up('Control');
  expect(await selected(panel)).toBe('1,2');
  expect(await panel.getAttribute('data-loop')).toBe('');
});

test('the Slice tool cuts every note the drag crosses, once each, in one edit', async ({ page }) => {
  const panel = await openStudio(page, 'sla', '1:0:2:60:96;pan=2 1:0:2:62:96');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('c');
  const a = await at(panel, 1, 62, 0), b = await at(panel, 1, 60, 0);
  const version = await versionOf();
  await page.mouse.move(a.x, a.y); await page.mouse.down();
  await page.mouse.move(b.x, b.y, { steps: 12 }); await page.mouse.up();
  await expect.poll(() => notesOf('sla')).toBe('1:0:1:60:96;pan=2 1:0:1:62:96 1:1:1:62:96 1:1:1:60:96;pan=2');
  expect(await versionOf()).not.toBe(version);
  await page.waitForTimeout(400);
  await page.keyboard.press('Control+z');
  await expect.poll(() => notesOf('sla')).toBe('1:0:2:60:96;pan=2 1:0:2:62:96');
});

test('Alt+arrows nudge the selection by a small step', async ({ page }) => {
  const panel = await openStudio(page, 'nua', '1:0:1:60:96');
  await clickNote(page, panel, 0, 60);
  await page.keyboard.press('Alt+ArrowRight');
  await expect.poll(async () => (await notesOf('nua'))?.startsWith('1:0.020833:1:60:96')).toBe(true);
});

test('each tool has its own cursor and the grid reports the tool', async ({ page }) => {
  const panel = await openStudio(page, 'cura', '1:0:1:60:96');
  const grid = panel.locator('canvas.mls-grid');
  await grid.focus();
  const cursor = () => grid.evaluate((g) => getComputedStyle(g).cursor);
  const seen = new Set<string>();
  for (const [key, tool] of [['p', 'draw'], ['b', 'paint'], ['d', 'delete'], ['c', 'slice'], ['e', 'select'], ['y', 'playback'], ['z', 'zoom']]) {
    await page.keyboard.press(key);
    await expect(grid).toHaveAttribute('data-tool', tool);
    seen.add(await cursor());
  }
  expect(seen.size).toBeGreaterThanOrEqual(6);
  await page.keyboard.press('p');
  expect(await cursor()).toContain('url(');
});

test('the first-run hint shows on an empty clip, is dismissed once and stays dismissed', async ({ page }) => {
  const panel = await openStudio(page, 'hinta');
  const hint = panel.locator('.mls-hint');
  await expect(hint).toBeVisible();
  await expect(hint).toContainText('Click to draw · right-click deletes · drag an edge to resize · ? for shortcuts');
  await hint.getByRole('button').click();
  await expect(hint).toBeHidden();
  await panel.locator('.mls-close').click();
  await expect(panel).toHaveCount(0);
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  await expect(page.locator('dialog.mls-panel')).toBeVisible();
  await expect(page.locator('dialog.mls-panel .mls-hint')).toBeHidden();
});

test('? opens the shortcuts card inside the panel and Esc closes only the card', async ({ page }) => {
  const panel = await openStudio(page, 'helpa', '1:0:1:60:96');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Shift+?');
  await expect(panel.locator('.mls-help')).toBeVisible();
  await expect(panel.locator('.mls-help')).toContainText('Ctrl+Q');
  await page.keyboard.press('Escape');
  await expect(panel.locator('.mls-help')).toBeHidden();
  await expect(panel).toBeVisible();
});

test('the view is remembered per block: zoom, scroll, snap, loop bracket and panel position', async ({ page }) => {
  const panel = await openStudio(page, 'vma', '1:0:1:60:96 4:0:1:64:96', [], '1/8');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Shift+3');
  const ruler = (await panel.locator('canvas.mls-ruler').boundingBox())!, v = await viewOf(panel);
  const b0 = Math.ceil(v.scrollX / v.pxBeat) + 1;
  await page.mouse.move(ruler.x + b0 * v.pxBeat - v.scrollX, ruler.y + 12); await page.mouse.down();
  await page.mouse.move(ruler.x + (b0 + 2) * v.pxBeat - v.scrollX, ruler.y + 12, { steps: 6 }); await page.mouse.up();
  const title = (await panel.locator('.mls-title').boundingBox())!;
  await page.mouse.move(title.x + 60, title.y + 8); await page.mouse.down(); await page.mouse.move(title.x + 160, title.y + 48, { steps: 5 }); await page.mouse.up();
  const before = await viewOf(panel), loop = await panel.getAttribute('data-loop'), left = (await panel.boundingBox())!.x;
  expect(loop).not.toBe('');
  await page.waitForTimeout(600);
  await panel.locator('.mls-close').click();
  await expect(panel).toHaveCount(0);
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const again = await litePanel(page, null);
  const restored = await viewOf(again);
  expect(restored.pxBeat).toBeCloseTo(before.pxBeat, 1);
  expect(restored.rowH).toBeCloseTo(before.rowH, 1);
  expect(Math.abs(restored.scrollX - before.scrollX)).toBeLessThan(1);
  expect(Math.abs(restored.scrollY - before.scrollY)).toBeLessThan(1);
  expect(await again.getAttribute('data-loop')).toBe(loop);
  expect(await again.locator('select.mls-snap').inputValue()).toBe('1/8');
  expect(Math.abs((await again.boundingBox())!.x - left)).toBeLessThan(3);
});

test('a manual scroll during playback pauses following until the next Play', async ({ page }) => {
  const panel = await openStudio(page, 'fpa', '1:0:1:60:96 8:0:1:64:96');
  const grid = panel.locator('canvas.mls-grid');
  await grid.focus();
  await page.keyboard.press('Space');
  await expect.poll(() => panel.getAttribute('data-follow')).toBe('on');
  const box = (await grid.boundingBox())!;
  await page.mouse.move(box.x + 100, box.y + 60);
  await page.mouse.wheel(0, 40);
  await expect.poll(() => panel.getAttribute('data-follow')).toBe('paused');
  await page.keyboard.press('Space');
  await page.keyboard.press('Space');
  await expect.poll(() => panel.getAttribute('data-follow')).toBe('on');
  await page.keyboard.press('Space');
});

test('a clip with a scale parameter opens without errors and keeps its notes', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', (error) => errors.push(error.message));
  const panel = await openStudio(page, 'scla', '1:0:1:60:96', [['scale', '"c:major"']]);
  await panel.locator('canvas.mls-grid').focus();
  await page.waitForTimeout(200);
  expect(await notesOf('scla')).toBe('1:0:1:60:96');
  expect(errors).toEqual([]);
});

const askDialog = (page: Page) => page.locator('#dlg');

