import { test, expect } from '@playwright/test';
import { freshServer, openApp, state } from '../../web/tests/support';

freshServer();

test('BUG-01 (fixed by 0381c83): clearing a multiline text parameter must remove the empty source parameter', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.locator('.node[data-id="drums"] .nh .t').click();
  await page.locator('#props [role="tab"][data-key="parameters"]').click();
  const field = page.locator('#props .prop-tabpanel.active .prop').filter({ has: page.locator('label', { hasText: /(^|\s)pattern\s*♪$/ }) });
  await expect(field.locator('input')).toHaveValue('beat-A');

  await field.locator('input').fill('new transcript');
  await field.locator('input').press('Tab');
  await expect.poll(async () => (await state(page)).text).toContain('pattern new transcript');
  await field.locator('button', { hasText: '…' }).click();
  await expect(page.locator('#dlg')).toHaveJSProperty('open', true);
  await page.locator('#dlg-extra textarea').fill('');
  await page.locator('#dlg-btns button', { hasText: 'Apply' }).click();

  await expect.poll(async () => (await state(page)).text).not.toContain('pattern');
  await expect(field.locator('input')).toBeEditable();
  const doc = await page.evaluate(async () => (await (await fetch('/api/doc?path=song.amplet')).json()));
  expect(doc.diagnostics.filter((d) => d.severity === 'error' || d.severity === 1)).toEqual([]);
});
