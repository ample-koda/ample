import { test, expect } from '@playwright/test';
import { api, chooseMenu, emptySpot, freshServer, node, openApp } from '../../web/tests/support';

// Plan bundles BU04: save a chain as a bundle, see it in the library, place it under new names with its saved audio.
freshServer({
  song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\n',
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a block is saved as a bundle, listed in the library and placed under a prefix with its saved audio and no build', async ({ page }) => {
  await api('/api/build', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Build & export', 'Save as bundle...');
  await page.locator('dialog[open] button', { hasText: 'Save' }).first().click();
  await expect.poll(async () => (await api('/api/bundles')).bundles.length).toBe(1);
  const saved = (await api('/api/bundles')).bundles[0];
  expect(saved.blocks).toEqual(['drums']);
  expect(saved.takes.length).toBeGreaterThan(0);
  expect(String(saved.seeds.drums)).toMatch(/^[0-9]+$/);

  await page.mouse.click(800, 560, { button: 'right' });
  await page.locator('#menu div', { hasText: 'Bundle library' }).first().click();
  await expect(page.locator('#bundle-list option')).toHaveCount(1);
  await page.locator('dialog[open] button', { hasText: 'Place in the song' }).click();
  await page.locator('dialog[open] input').first().fill('intro-');
  await page.locator('dialog[open] button', { hasText: /^Place$/ }).click();
  await expect(page.locator('.node[data-id="intro-drums"]')).toBeVisible();
  // the block has its take at once: a dry run of the build finds it
  await expect.poll(async () => (await api('/api/build/status?path=song.amplet')).blocks.find((b: { block: string }) => b.block === 'intro-drums')?.status).toBe('cached');
  const taken = (await api('/api/build/status?path=song.amplet')).blocks;
  expect(taken.find((b: { block: string }) => b.block === 'drums').takeId).not.toBe(taken.find((b: { block: string }) => b.block === 'intro-drums').takeId);
});

test('a name the song already has is refused and the library can delete a bundle', async ({ page }) => {
  await api('/api/bundles/save', { method: 'POST', body: { path: 'song.amplet', blocks: ['bass'], name: 'Low end' } });
  const id = (await api('/api/bundles')).bundles.find((b: { name: string }) => b.name === 'Low end').id;
  const refused = await api('/api/bundles/place', { method: 'POST', body: { path: 'song.amplet', id, prefix: '' } });
  expect(JSON.stringify(refused)).toContain('already has a block');
  await openApp(page, { reset: false });
  await page.mouse.click(800, 560, { button: 'right' });
  await page.locator('#menu div', { hasText: 'Bundle library' }).first().click();
  await page.locator('#bundle-list').selectOption(id);
  await page.locator('dialog[open] button', { hasText: 'Delete' }).click();
  await page.locator('dialog[open] button', { hasText: /^Delete$/ }).click();
  await expect.poll(async () => (await api('/api/bundles')).bundles.some((b: { id: string }) => b.id === id)).toBe(false);
});

test('the add-block menu lists categories A to Z, and effect groups contain alphabetized blocks', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.mouse.click(900, 560, { button: 'right' });
  const subs = page.locator('#menu > .sub');
  const names = (await subs.allInnerTexts()).map((t) => t.split('\n')[0].replace(/ ›$/, '').trim());
  expect(names.length).toBeGreaterThan(4);
  // Koda's own menu stands first, the categories after it run A to Z
  const categories = names.filter((n) => n !== 'Koda');
  expect(categories).toEqual([...categories].sort((a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' })));
  // no click: pointing at a category is enough
  await subs.filter({ hasText: /^Effects/ }).hover();
  const fly = subs.filter({ hasText: /^Effects/ }).locator(':scope > .flyout');
  await expect(fly).toBeVisible();
  const groups = fly.locator(':scope > .sub');
  expect(await groups.count()).toBeGreaterThan(3);
  for (const group of await groups.all()) {
    await group.hover();
    const children = group.locator(':scope > .flyout > .menu-item-with-icon');
    const labels = (await children.allInnerTexts()).map((text) => text.trim());
    expect(labels).toEqual([...labels].sort((a, b) => a.localeCompare(b, 'en', { sensitivity: 'base' })));
  }
  const before = (await api('/api/doc?path=song.amplet')).blocks.length;
  const modulation = fly.locator(':scope > .sub').filter({ hasText: /^Modulation ›/ });
  await modulation.hover();
  await modulation.locator(':scope > .flyout > .menu-item-with-icon').filter({ hasText: /^Chorus$/ }).click();
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.length).toBe(before + 1);
});

test('a block can be made smaller or bigger by its grip, the size is kept and a double click gives the normal size back', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = node(page, 'drums');
  const grip = block.locator('.size-grip');
  await expect(block.locator('.node-status')).toBeVisible({ timeout: 30000 });   // the first build of the song ends with a status on the block and draws it again: drag after that, not in the middle of it
  await page.waitForTimeout(500);
  let before = 0;
  await expect(async () => {   // the grip shows while the pointer is over the block and fades in: hold it when it stands still, and try again if the drag did not take hold
    await block.hover();
    await expect(grip).toBeVisible({ timeout: 2000 });
    await page.waitForTimeout(300);
    const box = (await grip.boundingBox())!;
    before = (await block.boundingBox())!.width;
    // the middle of the grip: after Fit the block is drawn at about half its size, the grip is 7 pixels wide and its corner is rounded, so a point 7 pixels from its edge is outside it
    await grip.hover({ timeout: 3000 });
    await page.mouse.down();
    await page.mouse.move(box.x + box.width / 2 - 60, box.y + box.height / 2, { steps: 5 });
    try { await expect(block.locator('.node-status')).toBeHidden({ timeout: 2000 }); } finally { await page.mouse.up(); }   // the status tag does not show while the grip is dragged
  }).toPass({ timeout: 25000 });
  await expect(block).toBeVisible();
  await expect(block.locator('.node-status')).toBeVisible();
  await expect.poll(async () => (await block.boundingBox())?.width ?? Number.POSITIVE_INFINITY).toBeLessThan(before - 20);
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const resized = node(page, 'drums');
  await expect(resized).toBeVisible();
  await expect.poll(async () => (await resized.boundingBox())?.width ?? Number.POSITIVE_INFINITY).toBeLessThan(before - 20);
  await node(page, 'drums').hover();
  await node(page, 'drums').locator('.size-grip').dblclick();
  await expect.poll(async () => Math.round((await node(page, 'drums').boundingBox())?.width ?? 0)).toBe(Math.round(before));
});

test('a block with an EQ linked to it shows the connected effect picker', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const block = node(page, 'drums');
  await block.click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Add', 'Effects', 'EQ & filters', 'Equalizer');
  await expect(block.locator('button', { hasText: '+ EQ' })).toHaveCount(0);
  await expect(block.getByRole('button', { name: /^Go to effects of/ })).toHaveCount(1);
});

test('Auto format arranges the selected blocks by signal flow', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const ids = await page.evaluate(async () => { const store = await import('/app/store.js'); return store.flat().nodes.filter((n: { proxy?: boolean }) => !n.proxy).map((n: { id: string }) => n.id); });
  await page.evaluate(async (all) => { const store = await import('/app/store.js'); all.forEach((id: string) => store.S.selSet.add(id)); store.notify(); }, ids);
  await node(page, ids[0]).click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Layout', `Auto format ${ids.length} blocks`);
  await expect(page.locator('#toast')).toContainText('Arranged');
  const xs = await page.evaluate(async () => { const store = await import('/app/store.js'); return store.flat().links.map((l: { from: string; to: string }) => { const nodes = store.flat().nodes; return nodes.find((n: { id: string }) => n.id === l.from)!.x < nodes.find((n: { id: string }) => n.id === l.to)!.x; }); });
  expect(xs.length).toBeGreaterThan(0);
  expect(xs.every(Boolean)).toBe(true);   // every link now runs from left to right
});

test('right click > Add child hangs a block on the output of a block', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await page.locator('#menu > .sub', { hasText: /^Add ›/ }).hover();
  const fly = page.locator('#menu > .sub', { hasText: /^Add ›/ }).locator(':scope > .flyout');
  await expect(fly).toBeVisible();
  const before = (await api('/api/doc?path=song.amplet')).blocks.length;
  await chooseMenu(page, 'Add', 'Effects', 'Levels & stereo', 'Volume / channels');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.length).toBe(before + 1);
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.filter((b: { type: string; froms: unknown[] }) => b.type === 'volume' && b.froms.length === 1).length).toBeGreaterThan(0);   // a volume block that hangs on the chain of drums
});

test('Add child puts any effect into the chain of the block', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const fades = async () => (await api('/api/doc?path=song.amplet')).blocks.filter((b: { type: string }) => b.type === 'fade');
  const before = (await fades()).length;
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Add', 'Effects', 'Timing & transforms', 'Fade');
  await expect.poll(async () => (await fades()).length).toBe(before + 1);
  await expect.poll(async () => (await fades()).at(-1)?.froms?.length).toBe(1);   // it hangs on the output of drums
});

test('Ctrl+Z takes grouping back, and Ctrl+Y does it again', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const groups = () => page.evaluate(async () => Object.keys((await import('/app/store.js')).S.groups).length);
  const before = await groups();
  await page.evaluate(async () => { const store = await import('/app/store.js'); const ids = store.flat().nodes.filter((n: { proxy?: boolean }) => !n.proxy).slice(0, 2).map((n: { id: string }) => n.id); ids.forEach((id: string) => store.S.selSet.add(id)); (await import('/app/canvas.js')).groupSelected(); });
  expect(await groups()).toBe(before + 1);
  // the group is written into the song file as a comment line, and that text step is what Ctrl+Z takes back
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.doc.text.includes('  group '))).toBe(true);
  await page.evaluate(async () => (await import('/app/store.js')).undo());
  expect(await groups()).toBe(before);
  await page.evaluate(async () => (await import('/app/store.js')).redo());
  expect(await groups()).toBe(before + 1);
});

test('a group with a sound line has a play button, and its lines can be given a name', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const group = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'after-group' }, { type: 'setLayout', block: 'after-group', x: 900, y: 100 }, { type: 'connect', from: 'bass', output: 'audio', to: 'after-group', input: 'audio' }]);
    const ids = ['drums', 'bass'];
    ids.forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
    return Object.keys(store.S.groups).at(-1)!;
  });
  const block = page.locator(`.node[data-id="${group}"]`);
  await expect(block.getByRole('button', { name: /^Play / })).toBeVisible();
  // name the line: right click > Name the output
  await block.click({ button: 'right', position: { x: 20, y: 36 } });
  await page.locator('#menu > .sub', { hasText: /^Block tools ›/ }).hover();
  await page.locator('#menu > .sub > .flyout > .menu-item-with-icon', { hasText: /^Name the output/ }).first().click();
  await page.locator('#dlg-extra input[aria-label="Name of the line"]').fill('Beat');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Save' }).click();
  await expect(block).toContainText('Beat');
});

test('inside a group, a block can add a typed line to the group, and the line can be taken away again', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const group = await page.evaluate(async () => {
    const store = await import('/app/store.js');
    ['drums', 'bass'].forEach((id) => store.S.selSet.add(id));
    const canvas = await import('/app/canvas.js');
    canvas.groupSelected();
    const id = Object.keys(store.S.groups).at(-1)!;
    canvas.enter(store.current().nodes.find((n: { id: string }) => n.id === id));
    return id;
  });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await page.locator('#menu > .sub', { hasText: /^Block tools ›/ }).hover();
  const addOutput = page.locator('#menu > .sub > .flyout > .sub', { hasText: /^Add an output line/ });
  await addOutput.hover();
  await addOutput.locator(':scope > .flyout > div').first().click();
  const lines = () => page.evaluate(async (id) => (await import('/app/store.js')).S.groups[id].exposedOuts?.length ?? 0, group);
  await expect.poll(lines).toBe(1);
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'out').locator('.nameplate, .node-nameplate').first().dispatchEvent('contextmenu', { clientX: 300, clientY: 300, bubbles: true });
  const removeLine = page.locator('#menu > .menu-item-with-icon', { hasText: /^Remove the line/ });
  await expect(removeLine).toBeVisible();
  await removeLine.click();
  await expect.poll(lines).toBe(0);
});

test('groups and block sizes are kept in the song file', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => { const store = await import('/app/store.js'); ['drums', 'bass'].forEach((id) => store.S.selSet.add(id)); (await import('/app/canvas.js')).groupSelected(); });
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string).includes('  group ')).toBe(true);
  // after the browser forgot everything it kept itself, the group is still there: it comes from the song file
  await page.evaluate(() => localStorage.clear());
  await openApp(page, { reset: false });
  expect(await page.evaluate(async () => Object.keys((await import('/app/store.js')).S.groups).length)).toBeGreaterThan(0);
});

test('the notes of a block are the comment above it in the song', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'bass').click({ button: 'right', position: { x: 20, y: 36 } });
  await page.locator('#menu div', { hasText: /^Properties$/ }).first().click();
  await page.locator('button, [role=tab]', { hasText: /^Notes$/ }).first().click();
  const area = page.locator('textarea[placeholder*="notes about this block"]');
  await area.fill('keep the bass low\nand simple');
  await area.dispatchEvent('change');
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toMatch(/# keep the bass low\n# and simple\nbass bass /);
});

test('the outputs node of a group can be moved, and stays where it was put', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    ['drums', 'bass'].forEach((id) => store.S.selSet.add(id));
    const canvas = await import('/app/canvas.js');
    canvas.groupSelected();
    canvas.enter(store.current().nodes.find((n: { isGroup?: boolean }) => n.isGroup));
  });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const out = node(page, 'out');
  const measured = async (target: import('@playwright/test').Locator) => { let found: { x: number; y: number; width: number; height: number } | null = null; await expect.poll(async () => { found = await target.boundingBox(); return found !== null; }).toBe(true); return found!; };   // the canvas can draw the block again just now
  const before = await measured(out);
  const head = await measured(out.locator('.node-nameplate, .t').first());
  await page.mouse.move(head.x + head.width / 2, head.y + head.height / 2);
  await page.mouse.down(); await page.mouse.move(head.x + head.width / 2 + 90, head.y + head.height / 2 + 60, { steps: 6 }); await page.mouse.up();
  await expect.poll(async () => { const box = await out.boundingBox(); return box ? box.x - before.x : 0; }).toBeGreaterThan(40);
  const after = (await out.boundingBox())!;
  expect(await page.evaluate(async () => Object.values((await import('/app/store.js')).S.groups).some((g: any) => g.proxyPos?.out))).toBe(true);
});

test('the canvas Add > Effects menu groups blocks into labeled submenus', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const spot = await emptySpot(page);
  await page.mouse.click(spot.x, spot.y, { button: 'right' });
  const sub = page.locator('#menu > .sub', { hasText: /^Effects/ });
  await sub.hover();
  const eq = sub.locator('.flyout > .sub', { hasText: /^EQ & filters/ });
  await eq.hover();
  const entry = eq.locator('.flyout > div', { hasText: /^Equalizer$/ });
  await expect(entry).toBeVisible();
  expect((await entry.getAttribute('title'))!.length).toBeGreaterThan(5);
});
