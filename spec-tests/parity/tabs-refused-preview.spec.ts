import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Preview tabs (Project settings) and a refused open: when the Source view keeps unapplied text and the person cancels the question, the open of the clicked file is refused.
// The tab that was shown must be active again, and the preview entry it had must still be there (a refused preview must not have replaced it).
freshServer({ files: { 'refuse-a.txt': 'alpha\n', 'refuse-b.txt': 'beta\n', 'tune-refuse.amplet': demoSong() } });

const songPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);
const row = (page: Page, path: string) => page.locator(`#b-project table.project-details tr[data-path="${path}"]`);

/** Preview tabs on, the detailed Project list, and refuse-a.txt open in a preview tab with typed text that is not applied. */
async function previewWithUnappliedText(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate(() => {
    localStorage.setItem('ample.tabs.preview', '1');
    // a kept recovery draft lets the switch go on without asking; these specs need the question, so the typed text is not kept as a draft
    const setItem = Storage.prototype.setItem;
    Storage.prototype.setItem = function (key: string, value: string) { if (String(key).startsWith('ample.source-draft:')) return; return setItem.call(this, key, value); };
  });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  if (!(await panel.locator('.project-details').isVisible())) await panel.getByRole('button', { name: 'Toggle detailed file list' }).click();
  await expect(panel.locator('.project-details')).toBeVisible();
  await row(page, 'refuse-a.txt').click();
  await expect(page.locator('#tabbar .tab.preview.on[data-path="refuse-a.txt"]')).toBeVisible();
  await expect.poll(() => page.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).value)).toBe('alpha\n');
  await page.locator('#se-ta').evaluate((e) => { const ta = e as HTMLTextAreaElement & { oninput?: () => void }; ta.value = 'alpha typed\n'; ta.oninput?.(); });
}

async function cancelTheQuestion(page: Page) {
  await expect(page.locator('#dlg-b')).toContainText('changes that are not applied');
  await page.locator('#dlg-btns button', { hasText: 'Cancel' }).click();
}

test('a refused preview of another text file keeps the tab that was shown and its preview entry', async ({ page }) => {
  await previewWithUnappliedText(page);
  await row(page, 'refuse-b.txt').click();
  await cancelTheQuestion(page);
  await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', 'refuse-a.txt');
  await expect(page.locator('#tabbar .tab.preview[data-path="refuse-a.txt"]')).toHaveCount(1);
  await expect(page.locator('#tabbar .tab[data-path="refuse-b.txt"]')).toHaveCount(0);
  expect(await page.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).value)).toBe('alpha typed\n');
});

test('a refused preview of a song keeps the tab that was shown, and the song does not open', async ({ page }) => {
  await previewWithUnappliedText(page);
  const song = await songPath(page);
  await row(page, 'tune-refuse.amplet').click();
  await cancelTheQuestion(page);
  await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', 'refuse-a.txt');
  await expect(page.locator('#tabbar .tab.preview[data-path="refuse-a.txt"]')).toHaveCount(1);
  await expect(page.locator('#tabbar .tab[data-path="tune-refuse.amplet"]')).toHaveCount(0);
  expect(await songPath(page)).toBe(song);
});
