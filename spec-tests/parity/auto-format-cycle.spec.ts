import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Pressing Auto format again on blocks that are already arranged (so it would do nothing) tries another layout: top to bottom, then in rows, then left to right again.
freshServer({
  song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\nmidi-clip chords at 1 for 4 {\n  chords Am F C G\n}\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\nbass bass at 1 for 4 {\n  from chords.notes -> notes\n}\n',
});

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('Auto format twice in a row gives a different layout the second time, and the third time another one', async ({ page }) => {
  const press = () => page.evaluate(async () => {
    const store = await import('/app/store.js');
    const canvas = await import('/app/canvas.js');
    const ids = store.flat().nodes.filter((n: { proxy?: boolean; isGroup?: boolean }) => !n.proxy && !n.isGroup).map((n: { id: string }) => n.id);
    store.S.selSet = new Set(ids);
    await canvas.autoArrangeSelected();
    return { toast: document.querySelector('#toast')?.textContent ?? '', places: store.flat().nodes.filter((n: { proxy?: boolean }) => !n.proxy).map((n: { id: string; x: number; y: number }) => `${n.id}@${n.x},${n.y}`).sort().join('|') };
  });
  const first = await press();
  expect(first.toast).toContain('left to right');
  const second = await press();
  expect(second.places).not.toBe(first.places);
  expect(second.toast).toContain('top to bottom');
  const third = await press();
  expect(third.places).not.toBe(second.places);
  expect(third.toast).toMatch(/in rows|left to right/);
});
