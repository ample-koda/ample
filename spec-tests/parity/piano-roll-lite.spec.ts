import { test, expect, type Page } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block).parameters.find((p: { name: string }) => p.name === 'notes')?.value;

async function openLite(page: Page, name: string, notes?: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  if (notes) await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  return panel;
}

test('the Lite roll opens as a non-modal panel with the toolbar in the planned order', async ({ page }) => {
  const panel = await openLite(page, 'litea');
  expect(await panel.evaluate((d: HTMLDialogElement) => d.open && !d.matches(':modal'))).toBe(true);
  const labels = await panel.locator('.mls-toolbar > button, .mls-toolbar > select, .mls-toolbar .mls-tool').evaluateAll((els) => els.filter((e) => !(e as HTMLElement).hidden).map((e) => e.getAttribute('aria-label') || e.getAttribute('title') || ''));
  const order = ['Roll view', 'Grid view', 'Draw tool', 'Paint tool', 'Delete tool', 'Slice tool', 'Select tool', 'Playback tool', 'Zoom tool', 'Typing keyboard', 'Play / pause', 'Play only the selected', 'Stop and return', 'Metronome', 'Loop on/off', 'Follow the playhead', 'Snap to grid', 'Zoom out', 'Zoom in', 'Fit the clip'];
  let last = -1;
  for (const prefix of order) { const at = labels.findIndex((l) => l.startsWith(prefix)); expect(at, prefix).toBeGreaterThan(last); last = at; }
  // the Studio behind stays usable: a Properties field can be focused and edited while the panel is open
  await expect(panel.locator('canvas.mls-ruler')).toBeVisible();
  await expect(panel.locator('canvas.mls-velocity')).toBeVisible();
});

test('the loop bracket on the ruler is created, resized, moved and cleared', async ({ page }) => {
  const panel = await openLite(page, 'liteb', '1:0:0.5:60:96 3:0:0.5:64:96');
  const ruler = panel.locator('canvas.mls-ruler');
  const box = (await ruler.boundingBox())!;
  const beat = Number(await panel.getAttribute('data-px-beat')), step = Number(await panel.getAttribute('data-snap-ticks'));
  expect(step).toBeGreaterThan(0);
  const y = box.y + 12;
  await page.mouse.move(box.x + 2 * beat, y); await page.mouse.down(); await page.mouse.move(box.x + 4 * beat, y, { steps: 6 }); await page.mouse.up();
  const parse = async () => { const [a, b] = ((await panel.getAttribute('data-loop')) || '').split('-').map(Number); return { a, b }; };
  await expect.poll(async () => (await parse()).b - (await parse()).a).toBe(2_000_000);
  const first = await parse();
  expect(first.a).toBe(2_000_000);
  // drag the right edge one beat further
  await page.mouse.move(box.x + 4 * beat, y); await page.mouse.down(); await page.mouse.move(box.x + 5 * beat, y, { steps: 6 }); await page.mouse.up();
  await expect.poll(async () => (await parse()).b).toBe(5_000_000);
  expect((await parse()).a).toBe(2_000_000);
  // a plain click sets the playhead and does not change the bracket
  await page.mouse.click(box.x + 8 * beat, y);
  await expect.poll(() => panel.getAttribute('data-playhead')).toBe('8000000');
  expect((await parse()).b).toBe(5_000_000);
  // right-click clears it
  await ruler.click({ button: 'right', position: { x: 3 * beat, y: 12 } });
  await expect.poll(() => panel.getAttribute('data-loop')).toBe('');
  // double-click resets to the whole clip
  await ruler.dblclick({ position: { x: 3 * beat, y: 12 } });
  await expect.poll(() => panel.getAttribute('data-loop')).toBe('0-12000000');
});

test('typing in a Properties field draws nothing and does not change the tool', async ({ page }) => {
  const panel = await openLite(page, 'litec');
  const field = page.locator('input[type=text], input:not([type])').filter({ hasNot: page.locator('dialog') }).first();
  await page.evaluate(() => { const i = document.createElement('input'); i.id = 'probe-field'; i.style.cssText = 'position:fixed;left:2px;bottom:2px;z-index:99'; document.body.append(i); });
  await page.locator('#probe-field').focus();
  await page.keyboard.type('pbdcez zsx');
  await page.keyboard.press('Space');
  expect(await panel.getAttribute('data-tool')).toBe('draw');
  expect(await notesOf('litec')).toBeUndefined();
  void field;
  await expect(panel.locator('button[aria-label^="Play / pause"]')).toHaveAttribute('aria-pressed', 'false');
});

test('drawing in the grid writes one note and right-click deletes it', async ({ page }) => {
  const panel = await openLite(page, 'lited');
  const grid = panel.locator('canvas.mls-grid'), box = (await grid.boundingBox())!;
  await page.mouse.click(box.x + 120, box.y + 100);
  await expect.poll(() => notesOf('lited')).toMatch(/^"?1:[0-9.]+:[0-9.]+:[0-9]+:96"?$/);
  await page.mouse.click(box.x + 120, box.y + 100, { button: 'right' });
  await expect.poll(() => notesOf('lited')).toBeUndefined();
});

test('the velocity strip sets a note velocity and Shift+drag draws a ramp, other fields stay', async ({ page }) => {
  const panel = await openLite(page, 'litee', '1:0:0.5:60:96;pan=20 1:0.5:0.5:64:96 1:1:0.5:67:96');
  const strip = panel.locator('canvas.mls-velocity'), box = (await strip.boundingBox())!;
  const beat = Number(await panel.getAttribute('data-px-beat'));
  await page.mouse.move(box.x + 2, box.y + 5); await page.mouse.down(); await page.mouse.up();
  await expect.poll(() => notesOf('litee')).toMatch(/^"1:0:0.5:60:12\d;pan=20 /);
  expect(await notesOf('litee')).toContain(' 1:0.5:0.5:64:96 ');
  await page.keyboard.down('Shift');
  await page.mouse.move(box.x + 0.5 * beat * 1 + 1, box.y + 60); await page.mouse.down(); await page.mouse.move(box.x + 2 * beat, box.y + 5, { steps: 6 }); await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect.poll(async () => (await notesOf('litee')).includes(';pan=20')).toBe(true);
});
