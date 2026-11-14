import { test, expect } from '@playwright/test';
import { api, chooseMenu, freshServer, node, openApp } from '../../web/tests/support';

// Pinned blocks: they keep their place by hand and in Auto format, and the rest is arranged around them.
freshServer({
  song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\n',
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a pinned block cannot be dragged, is kept in the song, and Auto format arranges the others around it @quarantine', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const pin = node(page, 'drums').getByRole('button', { name: 'Pin drums in place' });
  await expect(pin).toHaveAttribute('aria-pressed', 'false');
  await pin.click();
  await expect(node(page, 'drums').getByRole('button', { name: 'Unpin drums' })).toHaveAttribute('aria-pressed', 'true');
  await node(page, 'drums').getByRole('button', { name: 'Unpin drums' }).click();
  const place = (id: string) => page.evaluate(async (blockId) => { const n = (await import('/app/store.js')).flat().nodes.find((x: { id: string }) => x.id === blockId)!; return { x: n.x, y: n.y }; }, id);
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Layout', 'Pin in place');
  await expect(node(page, 'drums')).toHaveClass(/pinned/);
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toContain('  pin drums');
  // by hand: the drag does nothing
  const before = await place('drums');
  const head = (await node(page, 'drums').locator('.t').first().boundingBox())!;
  await page.mouse.move(head.x + head.width / 2, head.y + head.height / 2);
  await page.mouse.down(); await page.mouse.move(head.x + head.width / 2 + 140, head.y + head.height / 2 + 90, { steps: 6 }); await page.mouse.up();
  expect(await place('drums')).toEqual(before);
  // Auto format with everything selected: the pinned block stays, the others do not lie on it
  const ids = await page.evaluate(async () => { const store = await import('/app/store.js'); const all = store.flat().nodes.filter((n: { proxy?: boolean }) => !n.proxy).map((n: { id: string }) => n.id); all.forEach((id: string) => store.S.selSet.add(id)); store.notify(); return all; });
  await node(page, ids.find((id: string) => id !== 'drums')!).click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Layout', `Auto format ${ids.length} blocks`);
  await expect(page.locator('#toast')).toContainText('Arranged');
  expect(await place('drums')).toEqual(before);
  const boxes = await page.evaluate(async () => { const store = await import('/app/store.js'); const g = await import('/app/geometry.js'); return store.flat().nodes.map((n: any) => ({ id: n.id, x: n.x, y: n.y, w: g.nodeWidth(n), h: g.nodeHeight(n) })); });
  const pinned = boxes.find((b: { id: string }) => b.id === 'drums')!;
  for (const b of boxes.filter((x: { id: string }) => x.id !== 'drums')) {
    const overlap = b.x < pinned.x + pinned.w && pinned.x < b.x + b.w && b.y < pinned.y + pinned.h && pinned.y < b.y + b.h;
    expect(overlap, `${b.id} lies on the pinned block`).toBe(false);
  }
  // and unpinned again, it moves
  await page.evaluate(async () => { const store = await import('/app/store.js'); store.S.selSet.clear(); store.notify(); });
  await node(page, 'drums').click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Layout', 'Unpin');
  await expect(node(page, 'drums')).not.toHaveClass(/pinned/);
});
