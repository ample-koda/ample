import { test, expect, type Page, type Locator } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp } from '../../web/tests/support';

// MLS13 of AMPLE Jam: the scripted 12-step session and a small seeded UI fuzz over the real Lite panel. DOM only: pointer, wheel and keys,
// the panel's data attributes, the recorded player rows and the saved notes text. No audio is heard and no screenshot is taken: the feel items
// (acoustic latency, drag smoothness, cursors, wheel feel, hand-over between the modal Advanced dialog and the Lite panel) stay owner-observed.
freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const notesOf = async (block: string) => ((await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block)?.parameters.find((p: { name: string }) => p.name === 'notes')?.value as string | undefined)?.replace(/^"|"$/g, '') ?? '';
const tokens = async (block: string) => (await notesOf(block)).split(' ').filter(Boolean);
const edit = (page: Page, ops: unknown[]) => page.evaluate(async (list) => { const s = await import('/app/store.js'); await s.edit(list as never); }, ops);
const dbg = <T>(panel: Locator, fn: (d: any) => T) => panel.evaluate(fn as never) as Promise<T>;
const viewOf = (panel: Locator) => dbg(panel, (d) => ({ pxBeat: d.mlsDebug.state.pxBeat as number, rowH: d.mlsDebug.state.rowH as number, scrollX: d.mlsDebug.state.scrollX as number, scrollY: d.mlsDebug.state.scrollY as number }));
async function at(panel: Locator, beat: number, pitch: number, dx = 0.35) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!, v = await viewOf(panel);
  return { x: box.x + (beat + dx) * v.pxBeat - v.scrollX, y: box.y + (127 - pitch) * v.rowH - v.scrollY + v.rowH / 2 };
}
async function drag(page: Page, from: { x: number; y: number }, to: { x: number; y: number }) {
  await page.mouse.move(from.x, from.y); await page.mouse.down(); await page.mouse.move(to.x, to.y, { steps: 8 }); await page.mouse.up();
}
async function cell(panel: Locator, col: number, pitch: number) {
  const box = (await panel.locator('canvas.mls-grid').boundingBox())!;
  const pxBeat = Number(await panel.getAttribute('data-px-beat')), scrollX = Number(await panel.getAttribute('data-scroll-x')), step = Number(await panel.getAttribute('data-step-ticks')) / 1_000_000;
  const rows = ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').map(Number), rowH = Number(await panel.getAttribute('data-grid-row-h')), scrollY = Number(await panel.getAttribute('data-grid-scroll-y'));
  const i = rows.indexOf(pitch); expect(i, `row ${pitch} in ${rows}`).toBeGreaterThanOrEqual(0);
  return { x: box.x + (col + 0.5) * step * pxBeat - scrollX, y: box.y + i * rowH - scrollY + rowH / 2 };
}
/** A clip placed at bar 1 and a neighbour at bar 3, then the clip opened in Lite with a 1/4 snap. */
async function openSession(page: Page, name: string) {
  for (const n of [name, 'nbr']) await addTemplateBlock(page, 'midi-clip', n);
  await edit(page, [{ type: 'setParts', block: name, parts: [{ at: 1, length: 2, name: '' }] }, { type: 'setParts', block: 'nbr', parts: [{ at: 3, length: 2, name: '' }] }]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, name).locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  await panel.locator('select.mls-snap').selectOption('1/4');
  return panel;
}

test('the 12-step session: draw, move, resize, velocity, copy/paste, quantize, loop/play, Grid, Advanced, neighbour strip, undo/redo', async ({ page }) => {
  test.setTimeout(150_000);
  const timings: [string, number][] = [];
  let t0 = Date.now();
  const lap = (label: string) => { timings.push([label, Date.now() - t0]); t0 = Date.now(); };
  const name = 'acc';
  const panel = await openSession(page, name);
  const grid = panel.locator('canvas.mls-grid');

  // 1. an empty clip shows the hint
  await expect(panel.locator('.mls-hint')).toBeVisible();
  expect(await notesOf(name)).toBe('');
  await panel.locator('.mls-hint').getByRole('button').click();
  lap('1 hint');

  // 2. draw a four-note riff with clicks
  for (const [beat, pitch] of [[0, 60], [1, 64], [2, 67], [3, 64]]) { const p = await at(panel, beat, pitch); await page.mouse.click(p.x, p.y); }
  await expect.poll(async () => (await tokens(name)).length).toBe(4);
  expect((await tokens(name)).map((t) => t.split(':')[3]).join(',')).toBe('60,64,67,64');
  lap('2 draw');

  // 3. move the second note up a tone (body drag)
  await drag(page, await at(panel, 1, 64, 0.4), await at(panel, 1, 66, 0.4));
  await expect.poll(async () => (await tokens(name)).map((t) => t.split(':')[3]).join(',')).toBe('60,66,67,64');
  lap('3 move');

  // 4. resize the last note across the bar line (tied pieces)
  await drag(page, await at(panel, 3, 64, 0.93), await at(panel, 5, 64, 0));
  await expect.poll(async () => (await notesOf(name)).includes('tie=')).toBe(true);
  expect((await tokens(name)).filter((t) => t.split(':')[3] === '64' && t.includes('tie=')).length).toBe(2);
  lap('4 resize');

  // 5. velocity: Alt+wheel over the first note changes only that note
  const before = (await tokens(name))[0];
  const first = await at(panel, 0, 60);
  await page.mouse.move(first.x, first.y);
  await page.keyboard.down('Alt');
  for (let i = 0; i < 3; i++) await page.mouse.wheel(0, -100);
  await page.keyboard.up('Alt');
  await expect.poll(async () => (await tokens(name))[0]).not.toBe(before);
  expect(Number((await tokens(name))[0].split(':')[4].split(';')[0])).toBeGreaterThan(Number(before.split(':')[4].split(';')[0]));
  lap('5 velocity');

  // 6. copy and paste at the pointer
  await page.mouse.click(first.x, first.y);
  await page.keyboard.press('Control+c');
  const target = await at(panel, 6, 60);
  await page.mouse.move(target.x, target.y);
  await page.keyboard.press('Control+v');
  await expect.poll(async () => (await tokens(name)).some((t) => t.startsWith('2:2:') && t.split(':')[3] === '60')).toBe(true);
  lap('6 copy/paste');

  // 7. quantize: Alt draws off the grid, Ctrl+Q puts it on the grid and a second Ctrl+Q changes nothing
  const off = await at(panel, 7, 72, 0.37);
  await page.keyboard.down('Alt'); await page.mouse.click(off.x, off.y); await page.keyboard.up('Alt');
  await expect.poll(async () => (await tokens(name)).some((t) => t.split(':')[3] === '72')).toBe(true);
  await grid.focus();
  await page.keyboard.press('Control+a');
  await page.keyboard.press('Control+q');
  await expect.poll(async () => (await tokens(name)).find((t) => t.split(':')[3] === '72')!.split(':')[1]).toMatch(/^\d+$/);
  const quantized = await notesOf(name);
  await page.keyboard.press('Control+q');
  await page.waitForTimeout(300);
  expect(await notesOf(name)).toBe(quantized);
  await page.keyboard.press('Control+d');
  lap('7 quantize');

  // 8. loop bracket, play, an edit while it plays reaches the player, stop
  const ruler = (await panel.locator('canvas.mls-ruler').boundingBox())!, v = await viewOf(panel);
  await drag(page, { x: ruler.x + 1 * v.pxBeat - v.scrollX, y: ruler.y + 12 }, { x: ruler.x + 5 * v.pxBeat - v.scrollX, y: ruler.y + 12 });
  await expect.poll(async () => { const [a, b] = ((await panel.getAttribute('data-loop')) || '').split('-').map(Number); return b - a; }).toBe(4_000_000);
  await panel.getByRole('button', { name: /^Loop on\/off/ }).click();
  await panel.getByRole('button', { name: /^Play \/ pause/ }).click();
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'true', { timeout: 10_000 });
  const live = await at(panel, 4, 55);
  await page.mouse.click(live.x, live.y);
  await expect.poll(() => dbg(panel, (d) => d.mlsDebug.liveRows().some((r: { pitch: number }) => r.pitch === 55)), { timeout: 10_000 }).toBe(true);
  await grid.focus();
  await page.keyboard.press('Space');
  await expect(panel.getByRole('button', { name: /^Play \/ pause/ })).toHaveAttribute('aria-pressed', 'false');
  lap('8 loop/play');

  // 9. Grid: add a hi-hat row, paint a run, back to Roll: same notes plus the hats as bars
  const rollNotes = await notesOf(name);
  await panel.getByRole('button', { name: 'Grid view' }).click();
  await expect(panel).toHaveAttribute('data-mode', 'grid');
  expect(await notesOf(name)).toBe(rollNotes);
  await panel.locator('select.mls-rowadd').selectOption('42');
  await expect.poll(async () => ((await panel.getAttribute('data-grid-rows')) ?? '').split(',').includes('42')).toBe(true);
  await drag(page, await cell(panel, 0, 42), await cell(panel, 3, 42));
  await expect.poll(async () => (await tokens(name)).filter((t) => t.split(':')[3] === '42').length).toBe(4);
  await panel.getByRole('button', { name: 'Roll view' }).click();
  await expect(panel).toHaveAttribute('data-mode', 'roll');
  expect((await tokens(name)).length).toBe(rollNotes.split(' ').length + 4);
  lap('9 grid');

  // 10. Advanced and back: the notes, the zoom and the selection are kept
  await grid.focus();
  await page.keyboard.press('Control+d');
  const hit = await at(panel, 0, 60);
  await page.mouse.click(hit.x, hit.y);
  await expect.poll(() => panel.getAttribute('data-selected')).not.toBe('');
  const selection = (await panel.getAttribute('data-selected')) ?? '', zoom = (await viewOf(panel)).pxBeat, saved = await notesOf(name);
  const again = panel;
  await expect.poll(() => again.getAttribute('data-selected')).toBe(selection);
  expect((await viewOf(again)).pxBeat).toBeCloseTo(zoom, 1);
  expect(await notesOf(name)).toBe(saved);
  lap('10 advanced');

  // 11. neighbour strip: the neighbour opens in place, the Back chip returns
  await expect(again.locator('.mls-strip .mls-chip[data-clip="nbr"]')).toBeVisible();
  await again.locator('.mls-strip .mls-chip[data-clip="nbr"]').click();
  await expect(page.locator('dialog.mls-panel')).toHaveAttribute('aria-label', 'AMPLE Jam of nbr');
  expect(await notesOf(name)).toBe(saved);   // the strip edits nothing
  await page.locator('dialog.mls-panel .mls-strip .mls-back').click();
  const home = page.locator('dialog.mls-panel');
  await expect(home).toHaveAttribute('aria-label', `AMPLE Jam of ${name}`);
  lap('11 strip');

  // 12. undo and redo: the last edit (the hi-hat run) goes and comes back in single steps
  await home.locator('canvas.mls-grid').focus();
  await page.waitForTimeout(400);
  await page.keyboard.press('Control+z');
  await expect.poll(async () => (await tokens(name)).filter((t) => t.split(':')[3] === '42').length).toBe(0);
  await home.locator('canvas.mls-grid').focus(); await page.waitForTimeout(300);
  await page.keyboard.press('Control+Shift+z');
  await expect.poll(async () => (await tokens(name)).filter((t) => t.split(':')[3] === '42').length).toBe(4);
  lap('12 undo/redo');

  // budgets: no step is allowed to hang (generous, DOM-only; real feel is owner-observed)
  for (const [label, ms] of timings) expect(ms, `step ${label} took ${ms} ms`).toBeLessThan(20_000);
});

/** Small seeded generator so a failing run replays exactly. */
function rng(seed: number) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) >>> 0; let t = a; t = Math.imul(t ^ (t >>> 15), t | 1); t ^= t + Math.imul(t ^ (t >>> 7), t | 61); return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }

for (const seed of [11, 4242]) {
  test(`seeded UI fuzz over the Lite panel, seed ${seed}: the text stays well-formed with no duplicate start+pitch`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on('pageerror', (e) => errors.push(String(e)));
    const panel = await openSession(page, 'fuz');
    const grid = panel.locator('canvas.mls-grid');
    const next = rng(seed), log: string[] = [];
    const pick = (n: number) => Math.floor(next() * n);
    for (let i = 0; i < 24; i++) {
      const beat = pick(8), pitch = 60 + pick(12), roll = next();
      let action = '';
      if (roll < 0.35) { action = `click ${beat}/${pitch}`; const p = await at(panel, beat, pitch); await page.mouse.click(p.x, p.y); }
      else if (roll < 0.5) { action = `right ${beat}/${pitch}`; const p = await at(panel, beat, pitch); await page.mouse.click(p.x, p.y, { button: 'right' }); }
      else if (roll < 0.65) { const b2 = pick(8), p2 = 60 + pick(12); action = `drag ${beat}/${pitch}>${b2}/${p2}`; await drag(page, await at(panel, beat, pitch, 0.4), await at(panel, b2, p2, 0.4)); }
      else if (roll < 0.75) { action = 'undo'; await grid.focus(); await page.keyboard.press('Control+z'); }
      else if (roll < 0.82) { action = 'redo'; await grid.focus(); await page.keyboard.press('Control+Shift+z'); }
      else if (roll < 0.9) { action = 'select-all+quantize'; await grid.focus(); await page.keyboard.press('Control+a'); await page.keyboard.press('Control+q'); }
      else if (roll < 0.95) { action = 'select-all+duplicate'; await grid.focus(); await page.keyboard.press('Control+a'); await page.keyboard.press('Control+b'); }
      else { action = 'zoom'; const p = await at(panel, beat, pitch); await page.mouse.move(p.x, p.y); await page.keyboard.down('Control'); await page.mouse.wheel(0, next() < 0.5 ? -100 : 100); await page.keyboard.up('Control'); }
      log.push(action + ' => ' + (process.env.FUZZ_TRACE ? await notesOf('fuz') : ''));
      await page.waitForTimeout(60);
      const where = `seed ${seed}, step ${i}, last actions: ${log.slice(-5).join(' | ')}`;
      await expect(panel, where).toBeVisible();
      expect(errors, where).toEqual([]);
    }
    await page.waitForTimeout(500);   // let the last commit land
    const where = `seed ${seed}, last actions: ${log.slice(-5).join(' | ')}`;
    const list = await tokens('fuz'), seen = new Set<string>();
    // Every piece counts, a tied tail included (a note drawn or moved onto the tail of a tied note used to overlap it). Tails are counted so the run shows it met some.
    let tails = 0;
    for (const t of list) {
      if (/;tie=\d+/.test(t)) tails++;
      expect(t, where).toMatch(/^\d+:\d+(\.\d+)?:\d+(\.\d+)?:\d+:\d+(;[a-z]+=-?[\d.]+)*$/);
      const [bar, beat, , pitch] = t.split(':'), key = `${bar}:${beat}:${pitch}`;
      expect(seen.has(key), `${where}: duplicate ${key}`).toBe(false);
      seen.add(key);
      expect(Number(pitch), where).toBeLessThanOrEqual(127);
    }
    test.info().annotations.push({ type: 'tied pieces', description: String(tails) });
  });
}

// ---- the two optional acceptance steps (decisions 23 and 27) ----

/** A 16-bit mono WAV of a short tone: what the sound bank would answer, since the test server has no SoundFont. */
function tone(): Buffer {
  const n = 2000, buf = Buffer.alloc(44 + n * 2);
  buf.write('RIFF', 0); buf.writeUInt32LE(36 + n * 2, 4); buf.write('WAVEfmt ', 8); buf.writeUInt32LE(16, 16); buf.writeUInt16LE(1, 20); buf.writeUInt16LE(1, 22);
  buf.writeUInt32LE(44100, 24); buf.writeUInt32LE(88200, 28); buf.writeUInt16LE(2, 32); buf.writeUInt16LE(16, 34); buf.write('data', 36); buf.writeUInt32LE(n * 2, 40);
  for (let i = 0; i < n; i++) buf.writeInt16LE(Math.round(9000 * Math.sin(i / 8)), 44 + i * 2);
  return buf;
}

test('step 13: an instrument setting changed in Properties while the roll is open refills the preview cache, and the next trigger uses the new setting @quarantine', async ({ page }) => {
  test.setTimeout(90_000);
  const requests: { program: number }[] = [];
  await page.route('**/api/gm/audition', async (route) => { requests.push(route.request().postDataJSON()); await route.fulfill({ status: 200, contentType: 'audio/wav', body: tone() }); });
  await addTemplateBlock(page, 'midi-clip', 'ins');
  await addTemplateBlock(page, 'gm', 'voice');
  await edit(page, [{ type: 'connect', from: 'ins', output: 'notes', to: 'voice', input: 'notes' }, { type: 'setParameter', block: 'voice', name: 'program', value: '24' }, { type: 'setParts', block: 'ins', parts: [{ at: 1, length: 2, name: '' }] }]);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'ins').locator('.nh .t').click();
  await page.getByRole('button', { name: /AMPLE Jam/ }).click();
  const panel = page.locator('dialog.mls-panel');
  await expect(panel).toBeVisible();
  await expect.poll(() => panel.locator('canvas.mls-grid').evaluate((c: HTMLCanvasElement) => c.clientWidth)).toBeGreaterThan(300);
  // the cache fills for the connected instrument: the first renders use program 24
  const key = (await at(panel, 0, 60)), keys = (await panel.locator('canvas.mls-keys').boundingBox())!;
  const hear = async () => { await page.mouse.move(keys.x + 10, key.y); await page.mouse.down(); await page.waitForTimeout(120); await page.mouse.up(); };
  await expect.poll(async () => { await hear(); return requests.some((r) => r.program === 24); }, { timeout: 20_000 }).toBe(true);
  const revision = await dbg(panel, (d) => d.mlsDebug.previewRevision() as number);
  // the owner changes the program in Properties (the same setParameter edit its field writes) while the roll stays open
  await edit(page, [{ type: 'setParameter', block: 'voice', name: 'program', value: '40' }]);
  await page.waitForTimeout(500);
  await expect.poll(async () => { await hear(); return requests.some((r) => r.program === 40); }, { timeout: 20_000 }).toBe(true);
  expect(await dbg(panel, (d) => d.mlsDebug.previewRevision() as number)).toBeGreaterThan(revision);
  // after the change the audition no longer asks for program 24 on a fresh trigger
  const before = requests.length;
  await hear(); await page.waitForTimeout(300);
  expect(requests.slice(before).every((r) => r.program === 40)).toBe(true);
});

test('step 14: drawing past the clip end writes nothing, a drag across it stops at the marker, and the + bar control extends the clip view', async ({ page }) => {
  test.setTimeout(90_000);
  const panel = await openSession(page, 'edge');
  const grid = panel.locator('canvas.mls-grid');
  await expect(panel.locator('.mls-status')).toContainText('2 bars');
  await panel.locator('.mls-hint').getByRole('button').click();
  // a click just past the end marker (beat 8) draws nothing
  const past = await at(panel, 8, 60, 0.05);
  await page.mouse.click(past.x, past.y);
  await page.waitForTimeout(300);
  expect(await notesOf('edge')).toBe('');
  // a drag that starts in the last beat and runs over the marker is cut at the clip end
  await drag(page, await at(panel, 7, 60, 0.2), await at(panel, 8, 60, 0.1));
  await expect.poll(async () => (await tokens('edge')).length).toBe(1);
  expect((await tokens('edge'))[0]).toBe('2:3:1:60:96');
  // the end marker sits on the ruler: "+ bar" there lengthens the clip view by one bar, and drawing in the new bar works
  const ruler = (await panel.locator('canvas.mls-ruler').boundingBox())!, v = await viewOf(panel);
  await page.mouse.click(ruler.x + 8 * v.pxBeat - v.scrollX + 10, ruler.y + 12);
  await expect(panel.locator('.mls-status')).toContainText('3 bars');
  expect((await tokens('edge')).length).toBe(1);   // the control edits no note and no arrangement
  await grid.focus();
  await page.keyboard.press('Shift+4');
  const inside = await at(panel, 9, 64, 0.2);
  await page.mouse.click(inside.x, inside.y);
  await expect.poll(async () => (await tokens('edge')).some((t) => t.startsWith('3:1:') && t.split(':')[3] === '64')).toBe(true);
});
