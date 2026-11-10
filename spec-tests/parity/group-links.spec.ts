import { test, expect } from '@playwright/test';
import { api, chooseMenu, freshServer, node, openApp } from '../../web/tests/support';

// Connections at the ports of a group (a server of its own, apart from bundles.spec.ts).
freshServer({
  song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\n',
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a connection at the port of a group can be taken away from outside, and made again', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'outside' }, { type: 'setLayout', block: 'outside', x: 900, y: 300 }, { type: 'connect', from: 'bass', output: 'audio', to: 'outside', input: 'audio' }]);
    ['drums', 'bass'].forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
  });
  const reads = async () => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === 'outside').froms.length;
  expect(await reads()).toBe(1);
  // the link of the canvas starts at the group, not at a block; the line of the group is kept by hand so that it stays when the link is gone
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const group = Object.values(store.S.groups).at(-1) as { exposedOuts?: unknown[] };
    const bass = store.flat().nodes.find((n: { id: string }) => n.id === 'bass')!;
    const out = bass.outs.findIndex((o: string[]) => o[1] === 'audio');
    group.exposedOuts = [{ block: 'bass', out, name: bass.outs[out][0], type: 'audio' }];
    store.notify();
    const link = store.current().links.find((l: unknown[]) => l[2] === 'outside');
    await (await import('/app/canvas.js')).deleteLink(link);
  });
  await expect.poll(reads).toBe(0);
  // and made again by dragging from the output line of the group to the input of the block
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  // the board redraws after the link was taken away: wait until both ports are there and stand still before the drag starts
  const fromPort = page.locator('.node.grp .port.out').first(), toPort = page.locator('.node[data-id="outside"] .port.in').first();
  await expect(fromPort).toBeVisible();
  await expect(toPort).toBeVisible();
  await page.waitForTimeout(300);
  let from!: { x: number; y: number; width: number; height: number }, to!: typeof from;
  await expect(async () => {   // take both ports when they have a place and stand still
    const f1 = await fromPort.boundingBox(), t1 = await toPort.boundingBox();
    await page.waitForTimeout(200);
    const f2 = await fromPort.boundingBox(), t2 = await toPort.boundingBox();
    expect(f1 && t1 && f2 && t2).toBeTruthy();
    expect([f2!.x, f2!.y, t2!.x, t2!.y]).toEqual([f1!.x, f1!.y, t1!.x, t1!.y]);
    from = f2!; to = t2!;
  }).toPass({ timeout: 15000 });
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down(); await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 8 }); await page.mouse.up();
  await expect.poll(reads).toBe(1);
});

test('the menu of a group can build it', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => { const store = await import('/app/store.js'); ['chords', 'bass'].forEach((id) => store.S.selSet.add(id)); (await import('/app/canvas.js')).groupSelected(); });
  await page.locator('.node.grp').first().click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Build & export', 'Build the group');
  await expect(page.locator('#toast')).toContainText('Built', { timeout: 30000 });
  expect(await page.evaluate(async () => !!(await import('/app/store.js')).S.build.takes?.bass)).toBe(true);
});

test('a group with a built line shows its waveform', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'tail' }, { type: 'setLayout', block: 'tail', x: 900, y: 300 }, { type: 'connect', from: 'bass', output: 'audio', to: 'tail', input: 'audio' }]);
    ['chords', 'bass'].forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
    await store.build('bass');
    store.notify();
  });
  await expect(page.locator('.node.grp .player canvas')).toBeVisible();
});

test('a group says built when its blocks are built, and its waveform is drawn @quarantine', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'tail2' }, { type: 'setLayout', block: 'tail2', x: 900, y: 500 }, { type: 'connect', from: 'bass', output: 'audio', to: 'tail2', input: 'audio' }]);
    ['chords', 'bass'].forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
  });
  const group = page.locator('.node.grp').last();
  await expect(group.locator('.node-status')).toHaveAttribute('title', /^not built/);
  await group.click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Build & export', 'Build the group');
  await expect(group.locator('.node-status')).toHaveAttribute('title', /built|cache hit/, { timeout: 30000 });
  // something is drawn on the waveform
  await expect.poll(() => group.locator('.player canvas').evaluate((c: HTMLCanvasElement) => { const d = c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data; return d.some((v, i) => i % 4 === 3 && v > 0); })).toBe(true);
});

test('a built group has the scrub player of every block, and its menu renders the chosen line', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'tail3' }, { type: 'setLayout', block: 'tail3', x: 900, y: 700 }, { type: 'connect', from: 'bass', output: 'audio', to: 'tail3', input: 'audio' }]);
    ['chords', 'bass'].forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
    await store.build('bass');
    store.notify();
  });
  const group = page.locator('.node.grp').last();
  await expect(group.locator('.player .plbg')).toBeVisible();   // the waveform that can be scrubbed, like on any block
  expect(await group.locator('.nfoot > button').count()).toBe(0);   // and no second play button next to the status
  await group.click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Build & export', 'Render the chosen line to file...');
  await expect(page.locator('dialog, #dlg').filter({ hasText: /render|file|folder/i }).first()).toBeVisible();
});

test('a menu closes when the canvas behind it is zoomed, and stays when its own list is scrolled', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.mouse.click(900, 560, { button: 'right' });
  await expect(page.locator('#menu')).toBeVisible();
  await page.locator('#menu').hover();
  await page.mouse.wheel(0, 40);
  await expect(page.locator('#menu')).toBeVisible();   // its own list
  await page.waitForTimeout(450);   // the menu ignores outside moves for the first 400 ms after it opened
  await page.mouse.move(300, 300);
  await page.mouse.wheel(0, -120);
  await expect(page.locator('#menu')).toBeHidden();   // the canvas zoomed: the menu would be in the wrong place
});
