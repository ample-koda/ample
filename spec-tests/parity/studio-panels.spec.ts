import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, openApp } from '../../web/tests/support';

freshServer();

test('effect advanced controls open from the node and properties card', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'comp');
  const node = page.locator('.node[data-id="comp"]');
  await node.getByRole('button', { name: 'Advanced', exact: true }).click();
  const dialog = page.locator('#studio');
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('slider', { name: 'Threshold' }).first()).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await node.click();
  await page.locator('#props .fx-advanced').click();
  await expect(dialog).toBeVisible();
});

test('a knob keeps focus and changes on every arrow key press', async ({ page }) => {
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'comp2');
  await page.locator('.node[data-id="comp2"]').getByRole('button', { name: 'Advanced', exact: true }).click();
  const knob = page.locator('#studio').getByRole('slider', { name: 'Threshold' }).first();
  await knob.focus();
  const start = Number(await knob.getAttribute('aria-valuenow'));
  await page.keyboard.press('ArrowUp');
  await page.keyboard.press('ArrowUp');
  const after = page.locator('#studio').getByRole('slider', { name: 'Threshold' }).first();
  await expect(after).toBeFocused();
  await expect.poll(async () => Number(await after.getAttribute('aria-valuenow'))).not.toBe(start);
});
