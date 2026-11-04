import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Dragging a selection of several items onto a folder: the confirmation comes first and Cancel moves nothing. (A file of its own: a fresh project, so the tiles stay where the test expects them.)
freshServer();

type Upload = Window & { AMPLE_API: { uploadProject: (file: File, dir: string, conflict: string) => Promise<unknown> } };

const prepare = async (page: Page, names: string[]) => {
  await openApp(page, { reset: false });
  await api('/api/project/folder', { method: 'POST', body: { path: 'kit' } });
  for (const name of names) await page.evaluate(async (n) => (window as unknown as Upload).AMPLE_API.uploadProject(new File([new Uint8Array([0, 1, 2])], n), '', 'refuse'), name);
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  await expect(page.locator('.project-tile').filter({ hasText: names[names.length - 1] })).toBeVisible();
};
const tile = (page: Page, name: string) => page.locator('.project-tile').filter({ hasText: name });
// the middle of an audio tile holds its player: click the corner, which is the tile itself
const pick = (page: Page, name: string, options: { modifiers?: ('Control' | 'Shift')[]; button?: 'right' } = {}) => tile(page, name).click({ position: { x: 4, y: 4 }, ...options });
const filePaths = async () => ((await api('/api/project/files')) as { path: string }[]).map((f) => f.path);
const selectedNames = (page: Page) => page.locator('.project-tile.sel .project-item-name').allTextContents();

test('dragging a selection onto a folder tile asks first, and Cancel moves nothing', async ({ page }) => {
  await prepare(page, ['d1.wav', 'd2.wav']);
  await pick(page, 'd1.wav');
  await pick(page, 'd2.wav', { modifiers: ['Control'] });
  await tile(page, 'd1.wav').dragTo(tile(page, 'kit'), { sourcePosition: { x: 4, y: 4 }, targetPosition: { x: 4, y: 4 } });
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#dlg')).toContainText('2 items will move into kit');
  await page.locator('#dlg-btns button', { hasText: 'Cancel' }).click();
  const files = await filePaths();
  expect(files).toEqual(expect.arrayContaining(['d1.wav', 'd2.wav']));
  expect(files).not.toContain('kit/d1.wav');
});
