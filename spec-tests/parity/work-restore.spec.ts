import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The browser keeps the work in progress (text and undo history): a refresh brings the history back, and work the song does not have is asked about.
freshServer({ song: 'composition "beats" { ample 1  tempo 100  master-seed 3 }\ndrums drums at 1 for 4 {\n  pattern beat-A\n}\n' });

const stored = (page) => page.evaluate(() => { const k = Object.keys(localStorage).find((x) => x.startsWith('ample.work:')); return k ? JSON.parse(localStorage.getItem(k)!) : null; });

const seed = async (page, key: string, value: unknown) => {
  await page.evaluate(([k, v]) => sessionStorage.setItem('ample.test-work-restore-seed', JSON.stringify([k, v])), [key, JSON.stringify(value)] as [string, string]);
  await page.addInitScript(() => {
    const pending = sessionStorage.getItem('ample.test-work-restore-seed');
    if (pending) { const [k, v] = JSON.parse(pending); localStorage.setItem(k, v); sessionStorage.removeItem('ample.test-work-restore-seed'); }
  });
};

test.beforeEach(async ({ page }) => { await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } }); await openApp(page, { reset: false }); });

test('a refresh keeps the undo history without asking when the song still has the work', async ({ page }) => {
  await page.evaluate(async () => { const store = await import('/app/store.js'); await store.applyText(`${store.S.doc.text}# one\n`); await store.applyText(`${store.S.doc.text}# two\n`); });
  await expect.poll(async () => (await stored(page))?.undo?.length).toBe(2);
  await page.reload();
  await expect(page.locator('#dlg[open]')).toHaveCount(0);
  await expect.poll(() => page.evaluate(async () => (await import('/app/store.js')).S.undo.length)).toBe(2);
});

test('work the song does not have comes back without asking', async ({ page }) => {
  const saved = (await api('/api/doc?path=song.amplet')).text as string;
  const work = `${saved}# unsaved work\n`;
  const id = await page.evaluate(async () => (await import('/app/store.js')).S.project.id);
  const record = { v: 1, project: id, path: 'song.amplet', text: work, undo: [saved], redo: [], updated: Date.now() };
  await seed(page, `ample.work:${id}:song.amplet`, record);
  await page.reload();
  await expect(page.locator('.recovery-diff-dialog')).toHaveCount(0);
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toContain('# unsaved work');
  expect(await page.evaluate(async () => (await import('/app/store.js')).S.undo.length)).toBe(1);
});

test('work of this browser is back as unsaved changes; discarding them (closing the tab) leaves the saved file', async ({ page }) => {
  const saved = (await api('/api/doc?path=song.amplet')).text as string;
  const id = await page.evaluate(async () => (await import('/app/store.js')).S.project.id);
  const other = { v: 1, project: id, path: 'song.amplet', text: `${saved}# other\n`, undo: [], redo: [], updated: Date.now() };
  await seed(page, `ample.work:${id}:song.amplet`, other);
  await page.reload();
  await expect(page.locator('.recovery-diff-dialog')).toHaveCount(0);
  await expect.poll(async () => ((await api('/api/doc?path=song.amplet')).text as string)).toContain('# other');
  expect((await api('/api/doc?path=song.amplet')).dirty).toBe(true);   // unsaved: the person saves, or discards
  await api('/api/doc/revert', { method: 'POST', body: { path: 'song.amplet' } });
  expect(((await api('/api/doc?path=song.amplet')).text as string)).not.toContain('# other');
});
