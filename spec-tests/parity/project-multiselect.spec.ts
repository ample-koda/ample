import { test, expect, Page } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// Selecting several items of the project panel (click, Ctrl, Shift, rectangle), a menu with what fits all of them, and moving them into a folder
// after a confirmation that lists the moves first.
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

test('click, Ctrl+click and Shift+click select like a file manager', async ({ page }) => {
  await prepare(page, ['a1.wav', 'a2.wav', 'a3.wav', 'a4.wav']);
  await pick(page, 'a1.wav');
  await pick(page, 'a3.wav', { modifiers: ['Control'] });
  expect((await selectedNames(page)).sort()).toEqual(['a1.wav', 'a3.wav']);
  await pick(page, 'a1.wav');
  await pick(page, 'a4.wav', { modifiers: ['Shift'] });
  const range = (await selectedNames(page)).sort();
  expect(range).toContain('a1.wav');
  expect(range).toContain('a4.wav');
  expect(range.length).toBeGreaterThanOrEqual(4);
});

test('a rectangle on empty space selects the tiles it touches', async ({ page }) => {
  await page.setViewportSize({ width: 2400, height: 1100 });   // wide enough for one row of tiles, so the bottom of the panel is empty space
  // the files of the tests before this one are still in the project and would make a second row of tiles that fills the panel: this test starts from an empty project
  for (const f of (await api('/api/project/files')) as { path: string }[]) if (/.wav$/i.test(f.path)) await api(`/api/project/file?path=${encodeURIComponent(f.path)}`, { method: 'DELETE' });
  await prepare(page, ['r1.wav', 'r2.wav', 'r3.wav']);
  // Find empty space in one corner of the actual tile layout and drag across the opposite corner. The layout settles after the tiles load, so the tiles are measured again until there is empty space.
  const findEmpty = async () => {
  const targets = [(await tile(page, 'r1.wav').boundingBox())!, (await tile(page, 'r3.wav').boundingBox())!];
  return page.evaluate((boxes) => {
    const content = document.querySelector('#project-panel-content') as HTMLElement;
    const r = content.getBoundingClientRect();
    const left = Math.min(...boxes.map((box) => box.x)), top = Math.min(...boxes.map((box) => box.y));
    const right = Math.max(...boxes.map((box) => box.x + box.width)), bottom = Math.max(...boxes.map((box) => box.y + box.height));
    for (let y = Math.min(r.bottom, innerHeight) - 6; y > r.top + 6; y -= 3) for (let x = r.right - 12; x > r.left + 6; x -= 5) {
      const target = document.elementFromPoint(x, y);
      if (!target || !content.contains(target) || target.closest('.project-tile, tr[data-path], thead, button, input, a, select')) continue;
      // the strip under the tiles, from under the last tile of the selection (other tiles may stand to its right, so empty space to the right of them is not needed)
      if (x > right - 100 && y > bottom) return { x, y, endX: left - 4, endY: top - 4 };
      if (x < left && y < top) return { x, y, endX: right + 4, endY: bottom + 4 };
      if (x > right && y < top) return { x, y, endX: left - 4, endY: bottom + 4 };
      if (x < left && y > bottom) return { x, y, endX: right + 4, endY: top - 4 };
    }
    return null;
  }, targets);
  };
  await expect.poll(findEmpty, { timeout: 10000 }).not.toBeNull();
  const empty = await findEmpty();
  await page.mouse.move(empty!.x, empty!.y);
  await page.mouse.down();
  await page.mouse.move(empty!.endX, empty!.endY, { steps: 12 });
  await expect(page.locator('.project-marquee')).toBeVisible();
  await page.mouse.up();
  await expect.poll(selectedNames.bind(null, page)).toEqual(expect.arrayContaining(['r1.wav', 'r3.wav']));
});

test('a selection is moved into a folder after a confirmation that lists the moves', async ({ page }) => {
  await prepare(page, ['m1.wav', 'm2.wav']);
  await pick(page, 'm1.wav');
  await pick(page, 'm2.wav', { modifiers: ['Control'] });
  await pick(page, 'm2.wav', { button: 'right' });
  await expect(page.locator('#menu')).toContainText('2 items selected');
  await page.locator('#menu div', { hasText: 'Move to...' }).first().click();
  await page.locator('#dlg-extra input').fill('kit');
  await page.locator('#dlg-btns button', { hasText: 'Next' }).click();
  // the confirmation lists every move before anything happens
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await expect(page.locator('#move-plan')).toContainText('m1.wav  →  kit/m1.wav');
  await expect(page.locator('#move-plan')).toContainText('m2.wav  →  kit/m2.wav');
  expect((await filePaths()).includes('m1.wav')).toBe(true);   // still in the root
  await page.locator('#dlg-btns button', { hasText: 'Move' }).click();
  await expect.poll(async () => await filePaths()).toEqual(expect.arrayContaining(['kit/m1.wav', 'kit/m2.wav']));
  const files = await filePaths();
  expect(files).not.toContain('m1.wav');
  expect(files).not.toContain('m2.wav');
});
