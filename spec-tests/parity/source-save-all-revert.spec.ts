import { test, expect, type Page } from '@playwright/test';
import { api, dialog, freshServer, openApp } from '../../web/tests/support';

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
  await expect(page.locator('#se-ta')).toBeVisible();
  await page.locator('#se-ta').fill('typed but not applied\n');
  await page.evaluate(() => import('/app/tabs.js').then((m) => m.saveAll()));
  await expect.poll(async () => (await api('/api/file?path=notes.txt')).text).toBe('typed but not applied\n');
});

test('Revert in the Source editor puts the saved song back, also after Apply, and the song is no longer changed', async ({ page }) => {
  await page.click('#vs-source');
  const ta = page.locator('#se-ta');
  await expect(ta).toBeVisible();
  const saved = await ta.inputValue();
  await ta.fill(`${saved}\n# a line that is applied but not saved\n`);
  await page.click('#se-apply');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).dirty).toBe(true);

  await page.click('#se-revert');
  await dialog(page, 'Revert');
  await expect.poll(async () => (await api('/api/doc?path=song.amplet')).dirty).toBe(false);
  expect(await ta.inputValue()).toBe(saved);
  expect((await api('/api/doc?path=song.amplet')).text).toBe(saved);
});
