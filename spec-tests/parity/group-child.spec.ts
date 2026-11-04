import { test, expect } from '@playwright/test';
import { api, chooseMenu, freshServer, node, openApp } from '../../web/tests/support';

// A child on a group (a server of its own, apart from group-links.spec.ts).
freshServer({
  song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\n',
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a group can be given a child: on its line, or after choosing the line when there are several', async ({ page }) => {
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    await store.edit([{ type: 'addBlock', blockType: 'volume', name: 'after-bass' }, { type: 'setLayout', block: 'after-bass', x: 900, y: 100 }, { type: 'connect', from: 'bass', output: 'audio', to: 'after-bass', input: 'audio' }]);
    ['drums', 'bass'].forEach((id) => store.S.selSet.add(id));
    (await import('/app/canvas.js')).groupSelected();
  });
  // the group is written into the song first: later changes do not race with that
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.doc.text.includes('  group '))).toBe(true);
  const group = page.locator('.node.grp').last();
  const volumes = async () => (await api('/api/doc?path=song.amplet')).blocks.filter((b: { type: string }) => b.type === 'volume').length;
  const before = await volumes();
  // one line: the blocks are listed at once
  await group.click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Add', 'Effects', 'Levels & stereo', 'Volume / channels');
  await expect.poll(volumes).toBe(before + 1);
  // two lines: the lines are listed first, and a menu with the blocks comes up for the one that is chosen
  await page.waitForTimeout(700);   // what the first child wrote into the song settles first
  await page.evaluate(async () => {
    const store = await import('/app/store.js');
    const group = Object.values(store.S.groups).at(-1) as { exposedOuts?: unknown[] };
    const drums = store.flat().nodes.find((n: { id: string }) => n.id === 'drums')!;
    const out = drums.outs.findIndex((o: string[]) => o[1] === 'audio');
    group.exposedOuts = [{ block: 'drums', out, name: 'drums', type: 'audio' }];
    store.saveGroups();
  });
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.doc.text.includes('exposedOuts'))).toBe(true);
  await group.click({ button: 'right', position: { x: 20, y: 36 } });
  await chooseMenu(page, 'Add', 'From the line "drums" ›');
  await chooseMenu(page, 'Effects', 'Timing & transforms', 'Fade');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).blocks.filter((b: { type: string }) => b.type === 'fade').length).toBe(1);
});
