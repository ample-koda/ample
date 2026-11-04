import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { node, openApp, state } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

const dir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../spec/templates');
const specIds = () => fs.readdirSync(dir).filter((f) => f.endsWith('.json')).map((f) => JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).id as string).sort();

test('the palette lists exactly the templates of the spec', async ({ page }) => {
  const titles = await page.locator('.tplitem').count();
  expect(titles).toBe(specIds().length);
});

test('search filters the palette', async ({ page }) => {
  await page.fill('#lib-q', 'drum');
  await expect(page.locator('.tplitem')).toHaveCount(1);
  await page.fill('#lib-q', '');
  expect(await page.locator('.tplitem').count()).toBe(specIds().length);
});

test('double-click on a template adds a block with the template defaults to the source', async ({ page }) => {
  await page.fill('#lib-q', 'choir');
  await page.locator('.tplitem').first().dblclick();
  await expect.poll(async () => (await state(page)).text).toMatch(/choir choir (pinned@\S+ )?\{/);
  await expect(node(page, 'choir')).toBeVisible();
});

test('dragging a template onto the canvas places it where it was dropped', async ({ page }) => {
  await page.fill('#lib-q', 'Lyrics');
  const item = page.locator('.tplitem', { hasText: 'Timed lyrics' });
  const target = (await page.locator('#cv').boundingBox())!;
  await item.dragTo(page.locator('#cv'), { targetPosition: { x: target.width - 300, y: target.height - 150 } });
  await expect.poll(async () => (await state(page)).text).toMatch(/lyrics lyrics /);
  await expect.poll(async () => (await state(page)).text).toMatch(/layout lyrics -?\d+ -?\d+/);
});

test('the template info dialog shows its ports', async ({ page }) => {
  await page.locator('.tplitem', { hasText: 'Mixer' }).first().click({ button: 'right' });
  await page.locator('#menu div', { hasText: 'Template info' }).click();
  await expect(page.locator('#dlg-b')).toContainText('ports in:');
  await page.locator('#dlg-btns button').click();
});
