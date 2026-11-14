import { test, expect, type Page } from '@playwright/test';
import { api, dialog, freshServer, openApp, setSrc, srcText } from '../../web/tests/support';

// The Source editor: Save all saves the text that is typed there (#82) and Revert goes back to the saved text (#85).
freshServer({ files: { 'notes.txt': 'saved text\n' } });
test.beforeEach(async ({ page }) => { await openApp(page); });

const openFile = (page: Page, path: string) => page.evaluate((p) => new Promise<void>((resolve) => {
  const request = new CustomEvent('ample:open-file', { detail: p });
  const done = (event: Event) => {
    if ((event as CustomEvent).detail.request !== request) return;
    document.removeEventListener('ample:open-result', done);
    resolve();
  };
  document.addEventListener('ample:open-result', done);
  document.dispatchEvent(request);
}), path);

test('Save all writes the text that is typed in the Source editor of a text file', async ({ page }) => {
  await openFile(page, 'notes.txt');
  // the editor is CodeMirror: #se-ta is a stand-in that is read and set with evaluate
  await expect.poll(() => srcText(page)).toBe('saved text\n');
  await setSrc(page, 'typed but not applied\n');
  await page.evaluate(() => import('/app/tabs.js').then((m) => m.saveAll()));
  await expect.poll(async () => (await api('/api/file?path=notes.txt')).text).toBe('typed but not applied\n');
});

test('Revert in the Source editor puts the saved song back after the typed text reached the working copy', async ({ page }) => {
  await page.click('#vs-source');
  await expect.poll(() => srcText(page)).toMatch(/composition/);
  const saved = await srcText(page);
  // typing reaches the working copy after a short pause, while the editor still counts it as changed (Revert stays enabled)
  await setSrc(page, `${saved}\n# a line that is not saved\n`);
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).dirty).toBe(true);

  await page.click('#se-revert');
  await dialog(page, 'Revert');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).dirty).toBe(false);
  await expect.poll(() => srcText(page)).toBe(saved);
  expect((await api('/api/doc?path=song.amplet')).text).toBe(saved);
});
