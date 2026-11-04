import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServerPerTest, node, openApp, openTab } from '../../web/tests/support';

freshServerPerTest();
test.beforeEach(async ({ page }) => { await openApp(page); });

const valueOf = async (block: string, name: string) => (await api('/api/doc?path=song.amplet')).blocks.find((b: { name: string }) => b.name === block).parameters.find((p: { name: string }) => p.name === name)?.value;

test('the Automate button draws an auto(...) curve into the song and "Back to a plain value" removes it', async ({ page }) => {
  await addTemplateBlock(page, 'volume', 'vol');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'vol').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  await page.getByRole('button', { name: 'Automate gain' }).click();
  const plot = page.locator('svg.automation-plot');
  await expect(plot).toBeVisible();
  const box = (await plot.boundingBox())!;
  const at = (fx: number, fy: number) => ({ x: box.x + box.width * fx, y: box.y + box.height * fy });
  await page.mouse.click(at(0.3, 0.3).x, at(0.3, 0.3).y);
  await page.mouse.click(at(0.7, 0.7).x, at(0.7, 0.7).y);
  const points = page.getByRole('slider', { name: /^Point at bar/ });
  await expect.poll(() => points.count()).toBeGreaterThanOrEqual(3);   // the starting points plus the two clicks
  await page.locator('#dlg-btns').getByRole('button', { name: 'Apply' }).click();
  await expect.poll(() => valueOf('vol', 'gain')).toMatch(/^auto\(/);
  await page.getByRole('button', { name: 'Automate gain' }).click();
  await page.locator('#dlg-btns').getByRole('button', { name: 'Back to a plain value' }).click();
  await expect.poll(() => valueOf('vol', 'gain')).toBeUndefined();
});

test('the Fade... button writes a fadeout formula, opens again as the fade editor and the gain becomes a text field of the formula', async ({ page }) => {
  await addTemplateBlock(page, 'volume', 'vol');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'vol').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  await page.getByRole('button', { name: 'Automate gain' }).click();
  await page.locator('#dlg-btns').getByRole('button', { name: 'Fade...' }).click();
  await expect(page.locator('#dlg-t')).toContainText('Fade');
  await page.getByLabel('Fade in or out').selectOption('out');
  await page.getByLabel('Start', { exact: true }).fill('3');
  await page.getByLabel('Length', { exact: true }).fill('2');
  await page.getByLabel('Unit').nth(1).selectOption('s');
  await page.getByLabel('Shape').selectOption('equal');
  await expect(page.locator('svg.automation-plot path.studio-response-line')).toHaveAttribute('d', /M/);
  await page.locator('#dlg-btns').getByRole('button', { name: 'Apply' }).click();
  await expect.poll(() => valueOf('vol', 'gain')).toBe('fadeout(3, 2s, equal)');
  // the same editor opens for a formula that is already there; "Back to a plain value" removes it
  await page.getByRole('button', { name: 'Automate gain' }).click();
  await expect(page.getByLabel('Start', { exact: true })).toHaveValue('3');
  await page.locator('#dlg-btns').getByRole('button', { name: 'Back to a plain value' }).click();
  await expect.poll(async () => String(await valueOf('vol', 'gain'))).not.toMatch(/fade/);
});

test('the keyboard moves, adds and deletes automation points', async ({ page }) => {
  await addTemplateBlock(page, 'volume', 'vol');
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await node(page, 'vol').click({ position: { x: 20, y: 36 } });
  await openTab(page, 'parameters');
  await page.getByRole('button', { name: 'Automate gain' }).click();
  const points = page.getByRole('slider', { name: /^Point at bar/ });
  const before = await points.count();
  await points.first().focus();
  const value = Number(await points.first().getAttribute('aria-valuenow'));
  await page.keyboard.press('ArrowUp');
  await expect(points.first()).toBeFocused();
  expect(Number(await points.first().getAttribute('aria-valuenow'))).toBeGreaterThan(value);
  await page.keyboard.press('ArrowRight');
  await expect(points.first()).toHaveAttribute('aria-label', /Point at bar 2$/);
  await page.getByRole('button', { name: 'Add point' }).click();
  await expect(points).toHaveCount(before + 1);
  await page.keyboard.press('Delete');
  await expect(points).toHaveCount(before);
});
