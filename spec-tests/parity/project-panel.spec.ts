import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('Project panel switches tile and details views and filters project files', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  await expect(panel).toBeVisible();
  const projectTreeRoot = panel.locator('.project-panel-tree [role=treeitem]').first();
  await expect(projectTreeRoot).toHaveAttribute('aria-selected', 'true');
  await expect(projectTreeRoot).toHaveAttribute('data-path', '');
  await expect(panel.locator('.project-breadcrumb .crumb-part.current')).toHaveCount(1);
  await expect(panel.locator('.project-breadcrumb .crumb-part.current')).toHaveAttribute('title', 'Open the project folder');
  await projectTreeRoot.focus();
  await projectTreeRoot.press('ArrowLeft');
  await expect(projectTreeRoot).toHaveAttribute('aria-expanded', 'false');
  await projectTreeRoot.press('ArrowRight');
  await expect(projectTreeRoot).toHaveAttribute('aria-expanded', 'true');
  await expect(panel.locator('.project-tile')).toContainText('song.amplet');
  await panel.getByRole('searchbox', { name: 'Search project files' }).fill('missing-file');
  await expect(panel.locator('.project-tile')).toHaveCount(0);
  await panel.getByRole('searchbox', { name: 'Search project files' }).fill('');
  await panel.getByRole('button', { name: 'Toggle detailed file list' }).click();
  await expect(panel.locator('.project-details')).toBeVisible();
  await panel.getByRole('columnheader', { name: /Name/ }).click();
  const zoom = panel.getByRole('slider', { name: 'Project view size; far left is the detailed list' });
  await zoom.evaluate((slider: HTMLInputElement) => { slider.value = '4'; slider.dispatchEvent(new Event('input', { bubbles: true })); });
  await expect(panel.locator('.project-tile')).toBeVisible();
});

test('Project tree starts at the root and folders can be dragged into folders', async ({ page }) => {
  await openApp(page, { reset: false });
  const panel = page.locator('#b-project');
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const root = panel.locator('.project-panel-tree [role=treeitem]').first();
  await expect(root).toHaveAttribute('aria-selected', 'true');
  await expect(root).toHaveAttribute('data-path', '');
  await expect(panel.locator('.project-breadcrumb .crumb-part.current')).toHaveAttribute('title', 'Open the project folder');
  await page.evaluate(async () => {
    const api = (window as Window & { AMPLE_API: { createProjectFolder: (path: string) => Promise<unknown>; get: (path: string) => Promise<unknown> } }).AMPLE_API;
    await api.createProjectFolder('drag-source');
    await api.createProjectFolder('drag-target');
    const { refreshProjectPanel } = await import('/app/projectpanel.js');
    await refreshProjectPanel();
  });
  const visibleKinds = await panel.locator('.project-tile').evaluateAll((tiles) => tiles.map((tile) => tile.hasAttribute('data-dir')));
  const firstFolder = visibleKinds.indexOf(true);
  expect(firstFolder).toBeGreaterThan(0);
  expect(visibleKinds.slice(0, firstFolder).every((isFolder) => !isFolder)).toBe(true);
  const source = panel.locator('.project-panel-tree [role=treeitem][data-path="drag-source"]');
  const target = panel.locator('.project-panel-tree [role=treeitem][data-path="drag-target"]');
  await expect(source).toHaveAttribute('draggable', 'true');
  await source.dragTo(target);
  await page.locator('#dlg-btns button', { hasText: 'Move' }).click();
  await expect.poll(async () => {
    return page.evaluate(async () => (await (window as any).AMPLE_API.get('/api/project/tree?path=drag-target&depth=1')).children.some((item: { path: string }) => item.path === 'drag-target/drag-source'));
  }).toBe(true);
});

test('Project panel details support accessible resizing and optional project columns', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  await panel.getByRole('button', { name: 'Toggle detailed file list' }).click();
  const nameHeader = panel.getByRole('columnheader', { name: /Name/ });
  const grip = panel.getByRole('separator', { name: 'Resize Name column' });
  const width = Number(await grip.getAttribute('aria-valuenow'));
  await grip.focus();
  await grip.press('ArrowRight');
  await expect(grip).toHaveAttribute('aria-valuenow', String(width + 12));
  await nameHeader.click({ button: 'right' });
  await page.getByText('Add SHA-256 column', { exact: true }).click();
  await expect(panel.getByRole('columnheader', { name: /SHA-256/ })).toBeVisible();
});

test('Project details virtualize large folders and keep deep entries keyboard reachable', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  const zoom = panel.getByRole('slider', { name: 'Project view size; far left is the detailed list' });
  await zoom.focus(); await zoom.press('Home');
  await page.evaluate(async () => {
    const api = (window as Window & { AMPLE_API: { get: (path: string) => Promise<unknown> } }).AMPLE_API;
    const original = api.get.bind(api);
    const fakeFiles = Array.from({ length: 2101 }, (_, index) => ({ name: `asset-${String(index).padStart(4, "0")}.wav`, path: `asset-${String(index).padStart(4, "0")}.wav`, isDir: false, kind: 'audio', sizeBytes: 1024, modified: '2026-10-05T00:00:00Z' }));
    api.get = (path) => path === '/api/project/tree?path=&depth=1' ? Promise.resolve({ path: '', children: fakeFiles }) : path === '/api/project/tree?depth=1' ? Promise.resolve({ path: '', children: [{ name: 'samples', path: 'samples', isDir: true }] }) : original(path);
    const { refreshProjectPanel } = await import('/app/projectpanel.js');
    await refreshProjectPanel();
  });
  const host = panel.locator('#project-panel-content');
  expect(await host.locator('.project-details tbody tr[data-path]').count()).toBeLessThan(60);
  await host.evaluate((element) => { element.scrollTop = 40 * 1800; element.dispatchEvent(new Event('scroll')); });
  const deepRow = host.locator('[data-path="asset-1800.wav"]');
  await expect(deepRow).toBeAttached();
  await deepRow.focus(); await deepRow.press('End');
  await expect(host.locator('[data-path="asset-2100.wav"]')).toHaveAttribute('aria-selected', 'true');
});
