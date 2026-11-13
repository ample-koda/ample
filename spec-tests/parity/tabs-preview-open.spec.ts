import { test, expect, type Page } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Preview tabs (Project settings): a single click on a file in the detailed Project list opens it in a preview tab that the next single click replaces.
// A song goes through its own open route, so it must pass the tab check; and a tab is kept only when its file really opened.
freshServer({ files: { 'tune-a.amplet': demoSong(), 'tune-b.amplet': demoSong(), 'preview-fails.txt': 'text\n' } });

const songPath = (page: Page) => page.evaluate(async () => (await import('/app/store.js')).S.path);
const row = (page: Page, path: string) => page.locator(`#b-project table.project-details tr[data-path="${path}"]`);

async function detailedListWithPreviewTabs(page: Page) {
  await openApp(page, { reset: false });
  await page.evaluate(() => localStorage.setItem('ample.tabs.preview', '1'));
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  if (!(await panel.locator('.project-details').isVisible())) await panel.getByRole('button', { name: 'Toggle detailed file list' }).click();
  await expect(panel.locator('.project-details')).toBeVisible();
}

test('a single click on a song opens it in the preview tab, and a single click on the next song replaces that tab', async ({ page }) => {
  await detailedListWithPreviewTabs(page);
  expect(await songPath(page)).toBe('song.amplet');   // the songs clicked below are not the open one
  await row(page, 'tune-a.amplet').click();
  await expect(page.locator('#tabbar .tab.preview.on[data-path="tune-a.amplet"]')).toBeVisible();
  await expect.poll(() => songPath(page)).toBe('tune-a.amplet');
  await row(page, 'tune-b.amplet').click();
  await expect(page.locator('#tabbar .tab.preview.on[data-path="tune-b.amplet"]')).toBeVisible();
  await expect(page.locator('#tabbar .tab[data-path="tune-a.amplet"]')).toHaveCount(0);   // replaced, not added
  await expect.poll(() => songPath(page)).toBe('tune-b.amplet');
});

test('a preview whose open fails after the type check takes its tab back and keeps the tab that was shown', async ({ page }) => {
  await detailedListWithPreviewTabs(page);
  const before = await page.locator('#tabbar .tab.on').getAttribute('data-path');
  // the file list the text opener reads is not available: the open fails after the file passed the tab check
  await page.route('**/api/project/files', (route) => (route.request().method() === 'GET' ? route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ error: 'file list unavailable' }) }) : route.continue()));
  await row(page, 'preview-fails.txt').click();
  await expect(page.locator('#tabbar .tab[data-path="preview-fails.txt"]')).toHaveCount(0);
  await expect(page.locator('#tabbar .tab.on')).toHaveAttribute('data-path', String(before));
  const stored = await page.evaluate(async () => { const { S } = await import('/app/store.js'); return JSON.parse(localStorage.getItem(`ample.tabs:${S.project?.id || ''}`) || 'null'); });
  expect(stored?.active).toBe(before);
  expect(stored?.paths).not.toContain('preview-fails.txt');
  await page.unroute('**/api/project/files');
});
