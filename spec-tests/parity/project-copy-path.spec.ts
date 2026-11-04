import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The right-click menu of a project file has Copy with the name, the path in the project and the full path on disk; the preview window copies its path too.
freshServer({ files: { 'samples/storm.wav': 'RIFFfake' } });

const menuItem = (page: Page, text: string) => page.locator('#menu div', { hasText: text }).filter({ hasNot: page.locator('div', { hasText: text }) }).first();
const clipboard = (page: Page) => page.evaluate(() => navigator.clipboard.readText());

test('Copy gives the name, the path in the project and the full path', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await openApp(page, { reset: false });
  const folder = ((await api('/api/project')) as { folder: string }).folder;
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  await page.locator('.project-panel-tree [role=treeitem]', { hasText: 'samples' }).first().click();
  const tile = page.locator('.project-tile, tr[data-path]').filter({ hasText: 'storm.wav' }).first();
  const copy = async (label: string) => {
    await tile.click({ button: 'right' });
    await menuItem(page, 'Copy').hover();
    await menuItem(page, label).click();
  };
  await copy('Name');
  await expect.poll(() => clipboard(page)).toBe('storm.wav');
  await copy('Path in the project');
  await expect.poll(() => clipboard(page)).toBe('samples/storm.wav');
  await copy('Full path');
  const sep = folder.includes('\\') ? '\\' : '/';
  await expect.poll(() => clipboard(page)).toBe(`${folder.replace(/[\\/]+$/, '')}${sep}samples${sep}storm.wav`);
});
