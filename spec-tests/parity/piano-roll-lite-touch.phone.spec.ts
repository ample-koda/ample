import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MUI06: the Lite piano roll (AMPLE Jam) on a phone: full screen, scrollable big toolbar, pinch zoom, two-finger pan, Hand tool,
// one-finger draw/move, long press deletes, Grid view usable at 390 px. Touch is sent through CDP (real touch pointer events). Run with --project=phone.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block).parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '');

async function openLite(page: Page, name: string, notes?: string) {
  await addTemplateBlock(page, 'midi-clip', name);
  if (notes) await page.evaluate(async ([block, value]) => { const s = await import('/app/store.js'); await s.edit([{ type: 'setParameter', block, name: 'notes', value }]); }, [name, `"${notes}"`]);
  await page.getByRole('button', { name: 'Fit', exact: true }).tap();
  await node(page, name).locator('.nh .t').tap();
  await page.locator('button', { hasText: /AMPLE Jam/ }).first().evaluate((e) => (e as HTMLElement).click());
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(250);
  return panel;
}
const dbg = <T>(panel: Locator, fn: (d: any) => T) => panel.evaluate(fn as never) as Promise<T>;
const view = (panel: Locator) => dbg(panel, (d) => ({ ...d.mlsDebug.state }) as { pxBeat: number; rowH: number; scrollX: number; scrollY: number; gridRowH: number; gridScrollY: number; selected: Set<number> });
const noteCount = (panel: Locator) => dbg(panel, (d) => d.mlsDebug.rows().length as number);

async function at(panel: Locator, beat: number, pitch: number, dx = 0.35) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const v = await view(panel);
  return { x: box.x + (beat + dx) * v.pxBeat - v.scrollX, y: box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2 };
}
const pts = (list: { x: number; y: number }[]) => list.map((p, i) => ({ x: p.x, y: p.y, id: i }));
async function touch(page: Page) {
  const c = await page.context().newCDPSession(page);
  const send = (type: string, list: { x: number; y: number }[]) => c.send('Input.dispatchTouchEvent', { type, touchPoints: pts(list) } as never);
  return {
    start: (l: { x: number; y: number }[]) => send('touchStart', l),
    move: (l: { x: number; y: number }[]) => send('touchMove', l),
    end: () => send('touchEnd', []),
  };
}

test('the panel is full screen with a Back button, a big toolbar that scrolls sideways and a Hand tool', async ({ page }) => {
  const panel = await openLite(page, 'full', '1:0:1:60:96');
  const r = (await panel.boundingBox())!;
  expect(r.x).toBeLessThanOrEqual(1);
  expect(r.y).toBeLessThanOrEqual(1);
  expect(r.width).toBeGreaterThanOrEqual(389);
  expect(r.height).toBeGreaterThanOrEqual(843);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
  const back = (await panel.locator('.mls-close').boundingBox())!;
  expect(back.height).toBeGreaterThanOrEqual(44);
  expect(back.width).toBeGreaterThanOrEqual(44);
  expect(await panel.locator('.mls-close').evaluate((e) => getComputedStyle(e, '::after').content)).toContain('Back');
  const small = await panel.locator('.mls-toolbar button, .mls-toolbar select').evaluateAll((els) => els.filter((e) => e.getClientRects().length).map((e) => { const b = e.getBoundingClientRect(); return { t: e.getAttribute('aria-label') || e.textContent, w: Math.round(b.width), h: Math.round(b.height) }; }).filter((b) => b.w < 43.5 || b.h < 43.5));
  expect(small).toEqual([]);
  const bar = panel.locator('.mls-toolbar');
  expect(await bar.evaluate((e) => ({ over: getComputedStyle(e).overflowX, more: e.scrollWidth > e.clientWidth + 20 }))).toEqual({ over: 'auto', more: true });
  await bar.evaluate((e) => { e.scrollLeft = 400; });
  expect(await bar.evaluate((e) => e.scrollLeft)).toBeGreaterThan(100);
  await expect(panel.getByRole('button', { name: 'Hand tool' })).toBeVisible();
  expect(await panel.locator('.mls-strip').evaluate((e) => getComputedStyle(e).overflowX)).toBe('auto');
  expect(await panel.locator('canvas.mls-grid').evaluate((e) => getComputedStyle(e).touchAction)).toBe('none');
  await panel.locator('.mls-close').tap();
  await expect(panel).toBeHidden();
});

test('pinching spreads time or rows, two fingers moving together scroll, and the first finger leaves no note behind', async ({ page }) => {
  const panel = await openLite(page, 'pinch', '1:0:1:60:96 1:4:1:64:96 1:8:1:67:96');
  const t = await touch(page);
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  const before = await view(panel), count = await noteCount(panel);
  // spread horizontally: time zoom only
  await t.start([{ x: cx - 40, y: cy }, { x: cx + 40, y: cy }]);
  for (let i = 1; i <= 8; i++) await t.move([{ x: cx - 40 - i * 10, y: cy }, { x: cx + 40 + i * 10, y: cy }]);
  await t.end();
  const wide = await view(panel);
  expect(wide.pxBeat).toBeGreaterThan(before.pxBeat * 1.5);
  expect(Math.abs(wide.rowH - before.rowH)).toBeLessThan(before.rowH * 0.3);
  // spread vertically: rows only
  await t.start([{ x: cx, y: cy - 40 }, { x: cx, y: cy + 40 }]);
  for (let i = 1; i <= 8; i++) await t.move([{ x: cx, y: cy - 40 - i * 10 }, { x: cx, y: cy + 40 + i * 10 }]);
  await t.end();
  const tall = await view(panel);
  expect(tall.rowH).toBeGreaterThan(wide.rowH * 1.5);
  expect(Math.abs(tall.pxBeat - wide.pxBeat)).toBeLessThan(wide.pxBeat * 0.3);
  // two fingers moving left together scroll the time axis
  await t.start([{ x: cx + 60, y: cy }, { x: cx + 110, y: cy }]);
  for (let i = 1; i <= 8; i++) await t.move([{ x: cx + 60 - i * 12, y: cy }, { x: cx + 110 - i * 12, y: cy }]);
  await t.end();
  const panned = await view(panel);
  expect(panned.scrollX).toBeGreaterThan(tall.scrollX + 30);
  // the two-finger gestures drew nothing and the song did not change
  expect(await noteCount(panel)).toBe(count);
  await page.waitForTimeout(500);
  expect(await notesOf('pinch')).toBe('1:0:1:60:96 1:4:1:64:96 1:8:1:67:96');
});

test('a tap draws a note, a tap on a note selects it, a drag moves it, a long press deletes it', async ({ page }) => {
  const panel = await openLite(page, 'onefinger', '1:0:1:60:96');
  await panel.locator('select.mls-snap').selectOption('1/4');
  const t = await touch(page);
  // tap on empty ground draws (the Draw tool)
  const empty = await at(panel, 2, 64);
  await page.touchscreen.tap(empty.x, empty.y);
  await expect.poll(() => notesOf('onefinger')).toMatch(/^1:0:1:60:96 1:2:\d+:64:\d+$/);
  // tap on a note selects it and does not draw another
  const n = await at(panel, 0, 60, 0.3);
  await panel.getByRole('button', { name: 'Select tool' }).tap();
  await page.touchscreen.tap(n.x, n.y);
  expect(await dbg(panel, (d) => d.mlsDebug.state.selected.size as number)).toBe(1);
  expect(await noteCount(panel)).toBe(2);
  // a one-finger drag moves it by two beats
  const to = await at(panel, 3, 60, 0.3);
  await t.start([n]);
  for (let i = 1; i <= 10; i++) await t.move([{ x: n.x + ((to.x - n.x) * i) / 10, y: n.y }]);
  await t.end();
  await expect.poll(() => notesOf('onefinger')).toMatch(/^1:3:1:60:96 1:2:/);
  // a long press on a note deletes it
  const m = await at(panel, 3, 60, 0.3);
  await t.start([m]);
  await expect.poll(() => noteCount(panel), { timeout: 3000 }).toBe(1);
  await t.end();
  await expect.poll(() => notesOf('onefinger')).toMatch(/^1:2:\d+:64:\d+$/);
});

test('the Hand tool scrolls with one finger and draws nothing', async ({ page }) => {
  const panel = await openLite(page, 'hand', '1:0:1:60:96');
  await panel.getByRole('button', { name: 'Hand tool' }).tap();
  await expect(panel.getByRole('button', { name: 'Hand tool' })).toHaveAttribute('aria-pressed', 'true');
  // zoom in first so there is something to scroll
  await panel.getByRole('button', { name: 'Zoom in' }).tap();
  await panel.getByRole('button', { name: 'Zoom in' }).tap();
  const t = await touch(page);
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const x = box.x + box.width - 40, y = box.y + box.height / 2;
  const before = await view(panel);
  await t.start([{ x, y }]);
  for (let i = 1; i <= 8; i++) await t.move([{ x: x - i * 15, y: y + i * 5 }]);
  await t.end();
  const after = await view(panel);
  expect(after.scrollX).toBeGreaterThan(before.scrollX + 50);
  expect(after.scrollY).not.toBe(before.scrollY);
  expect(await noteCount(panel)).toBe(1);
  expect(await notesOf('hand')).toBe('1:0:1:60:96');
});

test('the Grid view works at 390 px: toggle a step by tap, long press accents, pinch changes the row size, Hand scrolls', async ({ page }) => {
  const panel = await openLite(page, 'gridy');
  await panel.getByRole('button', { name: 'Grid view' }).tap();
  await expect(panel).toHaveAttribute('data-mode', 'grid');
  await expect(panel.getByRole('button', { name: 'Hand tool' })).toBeVisible();
  await expect(panel.getByRole('button', { name: 'Draw tool' })).toBeHidden();
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  expect(box.width).toBeGreaterThan(250);
  expect(box.x + box.width).toBeLessThanOrEqual(390);
  const v0 = await view(panel);
  const x = box.x + 20, y = box.y + v0.gridRowH * 0.5;
  await page.touchscreen.tap(x, y);
  await expect.poll(() => noteCount(panel)).toBe(1);
  // long press on another cell: the step gets the accent velocity
  const t = await touch(page);
  const y2 = box.y + v0.gridRowH * 1.5;
  await t.start([{ x, y: y2 }]);
  await expect.poll(() => dbg(panel, (d) => d.mlsDebug.rows().map((r: { velocity: number }) => r.velocity) as number[]), { timeout: 3000 }).toContain(127);
  await t.end();
  expect(await noteCount(panel)).toBe(2);
  // pinch vertically: the row height grows
  const cx = box.x + box.width / 2, cy = box.y + box.height / 2;
  await t.start([{ x: cx, y: cy - 30 }, { x: cx, y: cy + 30 }]);
  for (let i = 1; i <= 6; i++) await t.move([{ x: cx, y: cy - 30 - i * 8 }, { x: cx, y: cy + 30 + i * 8 }]);
  await t.end();
  expect((await view(panel)).gridRowH).toBeGreaterThan(v0.gridRowH * 1.3);
  expect(await noteCount(panel)).toBe(2);
});
