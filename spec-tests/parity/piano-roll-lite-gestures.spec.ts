import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MLS05 / MLS06: gestures and live interaction of AMPLE Jam (DOM only: pointer, keys and the audition calls the panel records; no audio is heard here).
freshServerPerTest();
test.beforeEach(async ({ page }) => {
  // a fake MIDI keyboard, the same shape the Advanced roll's MIDI in uses (navigator.requestMIDIAccess -> inputs with onmidimessage)
  await page.addInitScript(() => {
    const input: { onmidimessage: ((m: { data: number[] }) => void) | null } = { onmidimessage: null };
    (window as unknown as { __fakeMidi: unknown }).__fakeMidi = { send: (data: number[]) => input.onmidimessage?.({ data }) };
    (navigator as unknown as { requestMIDIAccess: () => Promise<unknown> }).requestMIDIAccess = async () => ({ inputs: new Map([['fake', input]]), onstatechange: null });
  });
  await openApp(page);
});

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
  await panel.locator('select.mls-snap').selectOption('1/4');   // one beat, so every gesture lands on whole beats
  return panel;
}

/** Page coordinates of (beat, pitch) in the grid, from the panel's own view state. */
async function at(panel: Locator, beat: number, pitch: number, dx = 0.35) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const v = await panel.evaluate((d: HTMLElement & { mlsDebug: { state: { pxBeat: number; rowH: number; scrollX: number; scrollY: number } } }) => d.mlsDebug.state);
  return { x: box.x + (beat + dx) * v.pxBeat - v.scrollX, y: box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2, px: v.pxBeat };
}
const debug = <T>(panel: Locator, fn: (d: any) => T) => panel.evaluate(fn as never) as Promise<T>;
const calls = (panel: Locator) => debug(panel, (d) => d.mlsDebug.calls as { pitch: number; mono: boolean; held: number }[]);

test('dragging a note body moves it in time and pitch, keeps its fields, plays the new pitch and commits once', async ({ page }) => {
  const panel = await openLite(page, 'mova', '1:0:1:60:96;pan=20 1:2:1:64:90');
  const from = await at(panel, 0, 60), to = await at(panel, 1, 62);
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 12 }); await page.mouse.up();
  await expect.poll(() => notesOf('mova')).toBe('1:1:1:62:96;pan=20 1:2:1:64:90');
  expect((await calls(panel)).at(-1)!.pitch).toBe(62);
});

test('a click on a note that moves nothing leaves no edit', async ({ page }) => {
  const panel = await openLite(page, 'movb', '1:0:1:60:96 1:2:1:64:90');
  const before = await versionOf();
  const p = await at(panel, 0, 60);
  await page.mouse.click(p.x, p.y);
  await page.waitForTimeout(300);
  expect(await versionOf()).toBe(before);
  expect(await notesOf('movb')).toBe('1:0:1:60:96 1:2:1:64:90');
  expect(await panel.getAttribute('data-selected')).toBe('0');
});

test('dragging the right edge resizes across a bar line into tied segments and back again', async ({ page }) => {
  const panel = await openLite(page, 'resa', '1:0:1:60:96;pan=20 3:0:1:67:96');
  const edge = await at(panel, 0, 60, 0.93), to = await at(panel, 6, 60, 0);
  await page.mouse.move(edge.x, edge.y); await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 10 }); await page.mouse.up();
  await expect.poll(() => notesOf('resa')).toBe('1:0:4:60:96;pan=20;tie=0 2:0:2:60:96;pan=20;tie=0 3:0:1:67:96');
  // and back to two beats: the second segment goes away, the tie id with it
  const edge2 = await at(panel, 5.9, 60, 0), back = await at(panel, 2, 60, 0);
  await page.mouse.move(edge2.x, edge2.y); await page.mouse.down();
  await page.mouse.move(back.x, back.y, { steps: 10 }); await page.mouse.up();
  await expect.poll(() => notesOf('resa')).toBe('1:0:2:60:96;pan=20 3:0:1:67:96');
});

test('Shift+drag clones a note with its fields and leaves the original', async ({ page }) => {
  const panel = await openLite(page, 'clna', '1:0:1:60:96;fine=5');
  const from = await at(panel, 0, 60), to = await at(panel, 2, 60);
  await page.keyboard.down('Shift');
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 8 }); await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect.poll(() => notesOf('clna')).toBe('1:0:1:60:96;fine=5 1:2:1:60:96;fine=5');
});

test('double-click resets the velocity of a note to 100', async ({ page }) => {
  const panel = await openLite(page, 'vela', '1:0:1:60:50;pan=3 1:2:1:64:50');
  const p = await at(panel, 0, 60);
  await page.mouse.dblclick(p.x, p.y);
  await expect.poll(() => notesOf('vela')).toBe('1:0:1:60:100;pan=3 1:2:1:64:50');
});

test('a multi-selection moves, resizes and deletes together', async ({ page }) => {
  const panel = await openLite(page, 'mula', '1:0:1:60:96 1:2:1:64:96 3:0:1:67:96');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+a');
  await expect.poll(() => panel.getAttribute('data-selected')).toBe('0,1,2');
  const from = await at(panel, 0, 60), to = await at(panel, 1, 60);
  await page.mouse.move(from.x, from.y); await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 6 }); await page.mouse.up();
  await expect.poll(() => notesOf('mula')).toBe('1:1:1:60:96 1:3:1:64:96 3:1:1:67:96');
  const edge = await at(panel, 1, 60, 0.93), wide = await at(panel, 2, 60, 0.93);
  await page.mouse.move(edge.x, edge.y); await page.mouse.down(); await page.mouse.move(wide.x, wide.y, { steps: 6 }); await page.mouse.up();
  await expect.poll(() => notesOf('mula')).toBe('1:1:2:60:96 1:3:1:64:96;tie=0 2:0:1:64:96;tie=0 3:1:2:67:96');
  await page.keyboard.press('Delete');
  await expect.poll(() => notesOf('mula')).toBeUndefined();
});

test('Shift+arrows move the selection by a step or a semitone, Ctrl+Up by an octave, and the new pitch is heard', async ({ page }) => {
  const panel = await openLite(page, 'arra', '1:0:1:60:96');
  const p = await at(panel, 0, 60);
  await page.mouse.click(p.x, p.y);
  await page.keyboard.press('Shift+ArrowRight');
  await expect.poll(() => notesOf('arra')).toBe('1:1:1:60:96');
  await page.keyboard.press('Shift+ArrowUp');
  await expect.poll(() => notesOf('arra')).toBe('1:1:1:61:96');
  await page.waitForTimeout(200);   // the preview of a moved note is throttled (90 ms): a key that comes sooner is moved but not heard
  await page.keyboard.press('Control+ArrowUp');
  await expect.poll(() => notesOf('arra')).toBe('1:1:1:73:96');
  await expect.poll(async () => (await calls(panel)).some((c) => c.pitch === 73)).toBe(true);   // the preview of the new pitch is started a moment after the note has moved
});

test('holding the pointer past the right edge keeps scrolling while a note is dragged', async ({ page }) => {
  const panel = await openLite(page, 'autoa', '1:0:1:60:96 12:0:1:67:96');
  for (let i = 0; i < 5; i++) await panel.getByRole('button', { name: 'Zoom in' }).click();
  await debug(panel, (d) => { d.mlsDebug.state.scrollX = 0; });   // the zoom is anchored in the middle, so go back to the start
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const from = await at(panel, 0, 60);
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  await page.mouse.move(box.x + box.width - 4, from.y, { steps: 5 });
  await expect.poll(async () => Number(await panel.getAttribute('data-scroll-x'))).toBeGreaterThan(0);
  const first = Number(await panel.getAttribute('data-scroll-x'));
  await page.waitForTimeout(500);
  const later = Number(await panel.getAttribute('data-scroll-x'));
  expect(later).toBeGreaterThan(first);
  await page.mouse.up();
  await expect.poll(() => notesOf('autoa')).not.toMatch(/^1:0:1:60/);   // it followed the pointer into the scrolled space
});

test('drawing and moving audition through the bridge, throttled during a fast drag', async ({ page }) => {
  const panel = await openLite(page, 'audb');
  const p = await at(panel, 0, 62);
  await page.mouse.click(p.x, p.y);
  expect((await calls(panel)).at(-1)!.pitch).toBe(62);
  await expect.poll(() => notesOf('audb')).toMatch(/:62:96/);
  const before = (await calls(panel)).length;
  const q = await at(panel, 0, 62), up = await at(panel, 0, 70);
  await page.mouse.move(q.x, q.y); await page.mouse.down();
  await page.mouse.move(up.x, up.y, { steps: 40 }); await page.mouse.up();
  const made = (await calls(panel)).length - before;
  expect(made).toBeGreaterThanOrEqual(2);   // the press and at least the last pitch
  expect(made).toBeLessThan(10);            // 8 semitones in a few ms do not flood the audio
  expect((await calls(panel)).at(-1)!.pitch).toBe(70);
});

test('an edit made while playing is read by the player on its next round, and undo does not stop it', async ({ page }) => {
  const panel = await openLite(page, 'playa', '1:0:1:60:96');
  await panel.getByRole('button', { name: /^Loop on\/off/ }).click();
  await panel.getByRole('button', { name: /^Play \/ pause/ }).click();
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'true', { timeout: 10_000 });
  const p = await at(panel, 2, 67);
  await page.mouse.click(p.x, p.y);
  await expect.poll(() => debug(panel, (d) => d.mlsDebug.liveRows().map((r: { pitch: number }) => r.pitch).sort().join(','))).toBe('60,67');
  await panel.locator('canvas.mls-grid').focus();
  await page.waitForTimeout(400);   // the edit must be in the undo history before Ctrl+Z (seen racing once in a full run)
  await page.keyboard.press('Control+z');
  await expect.poll(() => debug(panel, (d) => d.mlsDebug.liveRows().map((r: { pitch: number }) => r.pitch).join(','))).toBe('60');
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'true');
  await page.keyboard.press('Space');
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'false');
});

test('notes and piano keys light up while they sound', async ({ page }) => {
  const panel = await openLite(page, 'lita', '1:0:4:60:96 1:0:4:67:96');
  await panel.getByRole('button', { name: /^Loop on\/off/ }).click();
  await panel.getByRole('button', { name: /^Play \/ pause/ }).click();
  await expect.poll(() => panel.getAttribute('data-lit'), { timeout: 10_000 }).toBe('0,1');
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('60,67');
  await panel.getByRole('button', { name: /^Stop and return/ }).click();
  await expect.poll(() => panel.getAttribute('data-lit')).toBe('');
});

test('pressing a piano key plays that pitch, held and monophonic, and dragging across keys retriggers', async ({ page }) => {
  const panel = await openLite(page, 'keya');
  const keys = panel.locator('canvas.mls-keys'), box = (await keys.boundingBox())!;
  const v = await debug(panel, (d) => d.mlsDebug.state as { rowH: number; scrollY: number });
  const yOf = (pitch: number) => box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2;
  await page.mouse.move(box.x + 20, yOf(64)); await page.mouse.down();
  expect((await calls(panel)).at(-1)).toMatchObject({ pitch: 64, mono: true });
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('64');
  await page.mouse.move(box.x + 20, yOf(65), { steps: 3 });
  expect((await calls(panel)).at(-1)).toMatchObject({ pitch: 65, mono: true });
  await page.mouse.up();
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('');
  expect(await notesOf('keya')).toBeUndefined();
});

test('the typing keyboard plays the planned pitches, [ ] and Alt+NumPad move the octave, and nothing is written', async ({ page }) => {
  const panel = await openLite(page, 'typa');
  await panel.locator('canvas.mls-grid').focus();
  await page.keyboard.press('Control+t');
  await expect(panel.getByRole('button', { name: /^Typing keyboard [(]Ctrl/ })).toHaveAttribute('aria-pressed', 'true');
  const last = async () => (await calls(panel)).at(-1)!.pitch;
  for (const [key, pitch] of [['z', 60], ['s', 61], ['x', 62], ['m', 71], [',', 72], ['q', 72], ['2', 73], ['u', 83]] as const) { await page.keyboard.press(key); expect(await last(), key).toBe(pitch); }
  await page.keyboard.press(']'); await page.keyboard.press('z'); expect(await last()).toBe(72);
  await page.keyboard.press('['); await page.keyboard.press('['); await page.keyboard.press('z'); expect(await last()).toBe(48);
  await page.keyboard.press('Alt+Numpad4'); await page.keyboard.press('z'); expect(await last()).toBe(60);
  await expect(panel.locator('.mls-octave')).toHaveAttribute('data-octave', '4');
  await page.keyboard.down('c'); await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('64'); await page.keyboard.up('c');
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('');
  expect(await panel.getAttribute('data-tool')).toBe('draw');
  expect(await notesOf('typa')).toBeUndefined();
});

test('a MIDI keyboard auditions through the roll while MIDI in is on and writes nothing', async ({ page }) => {
  const panel = await openLite(page, 'mida');
  const button = panel.getByRole('button', { name: /^MIDI in/ });
  await button.click();
  await expect(button).toHaveAttribute('aria-pressed', 'true');
  await page.evaluate(() => (window as unknown as { __fakeMidi: { send: (d: number[]) => void } }).__fakeMidi.send([0x90, 69, 100]));
  expect((await calls(panel)).at(-1)).toMatchObject({ pitch: 69 });
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('69');
  await page.evaluate(() => (window as unknown as { __fakeMidi: { send: (d: number[]) => void } }).__fakeMidi.send([0x80, 69, 0]));
  await expect.poll(() => panel.getAttribute('data-lit-keys')).toBe('');
  await button.click();
  await expect(button).toHaveAttribute('aria-pressed', 'false');
  expect(await notesOf('mida')).toBeUndefined();
});
