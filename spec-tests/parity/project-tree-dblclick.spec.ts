import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { freshServer, openApp } from '../../web/tests/support';

// A double click on a folder of the tree opens it and it stays open. The first click starts a refresh of the tree that draws the rows again, so the second click can land on a new row, and the
// refresh must not fold a folder that was opened while it was on its way.
freshServer({ prepare: (_cli, folder) => { fs.mkdirSync(path.join(folder, 'tree-parent', 'tree-child', 'tree-grand'), { recursive: true }); fs.writeFileSync(path.join(folder, 'tree-parent', 'tree-child', 'note.txt'), 'x'); } });

test('a double click expands a folder of the tree, also when the rows are drawn again between the clicks, and it stays expanded', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  const parent = panel.locator('.project-panel-tree [role=treeitem][data-path="tree-parent"]');
  await expect(parent).toBeVisible();
  // the tree of a big project answers slowly: the redraw comes between the two clicks, and the refresh is still running when the folder is opened
  await page.route('**/api/project/tree**', async (route) => { await new Promise((resolve) => setTimeout(resolve, 500)); await route.continue(); });
  const box = (await parent.boundingBox())!, x = box.x + box.width / 2, y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down({ clickCount: 1 }); await page.mouse.up({ clickCount: 1 });
  await page.waitForTimeout(250);
  await page.mouse.down({ clickCount: 2 }); await page.mouse.up({ clickCount: 2 });
  const child = panel.locator('.project-panel-tree [role=treeitem][data-path="tree-parent/tree-child"]');
  await expect(child).toBeVisible({ timeout: 5000 });
  // the refreshes that the clicks started have finished by now: the folder is still open
  await page.waitForTimeout(4000);
  await expect(child).toBeVisible();
});

test('a double click on a folder in the list opens it', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  const tile = panel.locator('#project-panel-content [data-path="tree-parent"]').first();
  await expect(tile).toBeVisible();
  const box = (await tile.boundingBox())!, x = box.x + box.width / 2, y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down({ clickCount: 1 }); await page.mouse.up({ clickCount: 1 });
  await page.waitForTimeout(250);
  await page.mouse.down({ clickCount: 2 }); await page.mouse.up({ clickCount: 2 });
  await expect(panel.locator('#project-panel-content [data-path="tree-parent/tree-child"]').first()).toBeVisible({ timeout: 5000 });
});

test('a double click on a folder inside an open folder opens it and it stays open', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.getByRole('button', { name: 'Project', exact: true }).click();
  const panel = page.locator('#b-project');
  const tree = (p: string) => panel.locator('.project-panel-tree [role=treeitem][data-path="' + p + '"]');
  await tree('tree-parent').dblclick();
  await expect(tree('tree-parent/tree-child')).toBeVisible({ timeout: 5000 });
  await tree('tree-parent/tree-child').dblclick();
  await expect(tree('tree-parent/tree-child/tree-grand')).toBeVisible({ timeout: 5000 });
  await page.waitForTimeout(2000);
  await expect(tree('tree-parent/tree-child/tree-grand')).toBeVisible();
  await expect(tree('tree-parent/tree-child')).toBeVisible();
});
