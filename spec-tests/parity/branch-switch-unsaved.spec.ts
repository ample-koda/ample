import { test, expect, type Page } from '@playwright/test';
import { api, bottomTab, dialog, freshServer, openApp, state } from '../../web/tests/support';

// Going to another branch must not leave unsaved work behind (issues #136 and #137).
freshServer({ files: { 'second.amplet': 'composition "second" { ample 1  tempo 111 master-seed 17 }\n' } });
test.beforeEach(async ({ page }) => { await openApp(page); });

const current = async () => (await api('/api/history/graph')).current as string;
const openSong = (page: Page, path: string) => page.evaluate(
  (p) => document.dispatchEvent(new CustomEvent('ample:open', { detail: p })), path);
const editOpenSong = (page: Page, text: string) => page.evaluate(
  (t) => import('/app/store.js').then((s) => s.applyText(s.S.doc.text + t)), text);

/** Makes a second branch and returns the name of the branch we were on before it, so there is somewhere to switch back to. */
async function forkAndReturnOther(page: Page) {
  const before = await current();
  await bottomTab(page, 'hist');
  await page.click('#btn-fork');
  await dialog(page, 'Fork', [`leaf-${Date.now()}`]);
  await expect.poll(current).not.toBe(before);
  return before;
}

const switchTo = (page: Page, line: string) => page.evaluate((l) => import('/app/history.js').then((m) => m.goToLine(l)), line);

test('a changed song in a background tab stops the branch switch and is named', async ({ page }) => {
  const other = await forkAndReturnOther(page);
  const here = await current();
  await openSong(page, 'second.amplet');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('second.amplet');
  await editOpenSong(page, '\n# an unsaved line\n');
  await openSong(page, 'song.amplet');
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('song.amplet');

  const switching = switchTo(page, other);   // the clean song has focus: the changed one is in the background
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#dlg')).toContainText('second.amplet');
  await dialog(page, 'Cancel');
  await switching;
  expect(await current()).toBe(here);
});

test('Save first that fails keeps the person on the branch with the text still in the editor', async ({ page }) => {
  const other = await forkAndReturnOther(page);
  const here = await current();
  await editOpenSong(page, '\n# keep me\n');
  await page.route('**/api/doc/save', (route) => route.fulfill({
    status: 409, contentType: 'application/json', body: JSON.stringify({ message: 'disk conflict' }),
  }));

  const switching = switchTo(page, other);
  await dialog(page, 'Save first');
  await switching;
  expect(await current()).toBe(here);
  expect((await state(page)).text).toContain('# keep me');
});
