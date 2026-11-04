import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

freshServer();

/** Switches to the project defaults and waits until the form has been drawn again for that scope. */
async function projectDefaults(page) {
  const button = page.getByRole('button', { name: 'New songs (project defaults)' });
  await button.click();
  await expect(button).toHaveClass(/on/);
}
test.beforeEach(async ({ page }) => {
  await openApp(page);
  await expect(page.locator('#plugins .pgear').first()).toBeVisible();
  await page.locator('#plugins .prow', { hasText: 'acestep' }).locator('.pgear').click();
  await expect(page.locator('#pg')).toBeVisible();
});

test('the form is generated from the settings schema of the manifest', async ({ page }) => {
  for (const key of ['endpoint', 'allowRemote', 'pollMs', 'timeoutS', 'style']) await expect(page.locator(`[data-setting="${key}"]`)).toBeVisible();
  await expect(page.locator('[data-field="allowRemote"]')).toHaveAttribute('type', 'checkbox');
  await expect(page.locator('[data-field="pollMs"]')).toHaveAttribute('type', 'number');
  await expect(page.locator('[data-field="endpoint"]')).toHaveValue('http://127.0.0.1:8085');
});

test('values are checked against the schema and nothing is stored while they are wrong', async ({ page }) => {
  await page.locator('[data-field="pollMs"]').fill('5');
  await expect(page.locator('#pg-errors')).toContainText('at least 20');
  await page.click('#pg-apply');
  await expect(page.locator('#toast')).toContainText('Fix first');
  await expect(page.locator('#pg')).toBeVisible();
  expect((await api('/api/plugins/settings?id=acestep')).values.pollMs).toBe(1000);
});

test('apply and back stores the settings in the project and they come back', async ({ page }) => {
  await projectDefaults(page);
  await page.locator('[data-field="pollMs"]').fill('500');
  await page.locator('[data-field="style"]').fill('warm choir');
  await page.click('#pg-apply');
  await expect(page.locator('#pg')).toBeHidden();
  const stored = (await api('/api/plugins/settings?id=acestep')).values;
  expect(stored.pollMs).toBe(500);
  expect(stored.style).toBe('warm choir');
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.locator('#plugins .prow', { hasText: 'acestep' }).locator('.pgear').click();
  await projectDefaults(page);
  await expect(page.locator('[data-field="pollMs"]')).toHaveValue('500');
});

test('undo, redo and reset to defaults work, and undo and back keeps nothing', async ({ page }) => {
  await page.locator('[data-field="pollMs"]').fill('777');
  await page.locator('[data-field="style"]').fill('changed');
  await expect(page.locator('#pg-status')).toContainText('not saved');
  await page.click('#pg-reset');
  await expect(page.locator('[data-field="pollMs"]')).toHaveValue('1000');
  await page.click('#pg-undo');
  await expect(page.locator('[data-field="pollMs"]')).not.toHaveValue('1000');
  await page.click('#pg-redo');
  await expect(page.locator('[data-field="pollMs"]')).toHaveValue('1000');
  await page.locator('[data-field="pollMs"]').fill('888');
  await page.click('#pg-undoback');
  await page.locator('#dlg-btns button', { hasText: 'Discard changes' }).click();
  await expect(page.locator('#pg')).toBeHidden();
  expect((await api('/api/plugins/settings?id=acestep')).values.pollMs).not.toBe(888);
});

test('another plugin can be chosen in the list on the left', async ({ page }) => {
  await expect(page.locator('.pgnav button[data-plugin="acestep"]')).toHaveClass(/on/);
  const other = page.locator('.pgnav button').nth(1);
  await other.click();
  await expect(other).toHaveClass(/on/);
});
