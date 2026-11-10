import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// A checkpoint stops when the recovery copy of a file in this browser differs from the file on the server. The person chooses: open the file, keep the copy of the browser
// (it is saved as the file) or use the saved file (the copy is thrown away). The checkpoint then goes on.
const SAVED = (tempo: number) => `composition "q" { ample 1  tempo ${tempo}  master-seed 5 }\n`;
freshServer({ song: 'composition "p" { ample 1  tempo 100  master-seed 3 }\n', files: { 'Testjes/A-first.amplet': SAVED(80), 'Testjes/Keep.amplet': SAVED(90), 'Testjes/Use.amplet': SAVED(90), 'Testjes/Open.amplet': SAVED(90) } });   // one file for each test: they share the server; the app opens the first one, and the file that is open is saved the normal way, so no test uses it
test.beforeEach(async ({ page }) => { await openApp(page, { reset: false }); });

const BROWSER_COPY = 'composition "q" { ample 1  tempo 120  master-seed 5 }\n';

async function plantDraft(page: import('@playwright/test').Page, file: string) {
  await expect.poll(() => api(`/api/doc?path=${file}`).then(() => true, () => false), { timeout: 15000 }).toBe(true);   // the server knows the file before the browser has a copy of it
  await page.evaluate(async ({ text, path }) => {
    const { S } = await import('/app/store.js');
    const project = S.project?.id || '';
    localStorage.setItem(`ample.work:${project}:${path}`, JSON.stringify({ v: 1, project, path, text, undo: [], redo: [], updated: Date.now() }));
  }, { text: BROWSER_COPY, path: file });
}

async function startCheckpoint(page: import('@playwright/test').Page) {
  await page.click('#btn-cp');
  await page.locator('#dlg-btns button', { hasText: 'Save all work and checkpoint' }).click();
  await expect(page.locator('#dlg-title, #dlg h2, #dlg').first()).toContainText('Checkpoint stopped');
}

test('the checkpoint that stops on a recovery copy offers to keep the copy of the browser', async ({ page }) => {
  await plantDraft(page, 'Testjes/Keep.amplet');
  await startCheckpoint(page);
  await page.locator('#dlg-btns button', { hasText: 'Keep my browser copy' }).click();
  await expect(page.locator('#toast')).toContainText('Checkpoint taken');
  expect((await api('/api/doc?path=Testjes/Keep.amplet')).text).toBe(BROWSER_COPY);
});

test('the checkpoint that stops on a recovery copy can use the saved file instead', async ({ page }) => {
  await plantDraft(page, 'Testjes/Use.amplet');
  await startCheckpoint(page);
  await page.locator('#dlg-btns button', { hasText: 'Use the saved file' }).click();
  await expect(page.locator('#toast')).toContainText(/Checkpoint taken|Nothing changed/);
  expect((await api('/api/doc?path=Testjes/Use.amplet')).text).toContain('tempo 90');
});

test('the checkpoint that stops on a recovery copy can open the file to look first', async ({ page }) => {
  await plantDraft(page, 'Testjes/Open.amplet');
  await startCheckpoint(page);
  await page.locator('#dlg-btns button', { hasText: 'Open the file' }).click();
  await expect(page.locator('#tabbar .tab[data-path="Testjes/Open.amplet"]')).toBeVisible({ timeout: 10000 });
});
