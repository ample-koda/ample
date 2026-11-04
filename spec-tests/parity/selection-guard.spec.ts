import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

const select = (page, selector: string) => page.locator(selector).first().evaluate((e) => getComputedStyle(e).userSelect);

test('the text of buttons and labels cannot be selected by accident, fields and logs still can, and the help menu switches it off and on', async ({ page }) => {
  await openApp(page);
  expect(await page.evaluate(() => document.body.classList.contains('no-select'))).toBe(true);
  expect(await select(page, '#btn-preview')).toBe('none');
  expect(await select(page, 'header h1, #brand')).toBe('none');
  await page.locator('#btn-help').click();
  const toggle = page.getByRole('menuitemcheckbox', { name: 'Stop accidental text selection' });
  await expect(toggle).toHaveAttribute('aria-checked', 'true');
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-checked', 'false');
  expect(await select(page, '#btn-preview')).not.toBe('none');
  await toggle.click();
  expect(await select(page, '#btn-preview')).toBe('none');
  await page.reload();
  expect(await select(page, '#btn-preview')).toBe('none');
  // what is meant to be read or copied stays selectable
  await page.evaluate(() => { const t = document.createElement('textarea'); t.id = 'probe-ta'; document.body.append(t); const p = document.createElement('pre'); p.id = 'probe-pre'; document.body.append(p); });
  expect(await select(page, '#probe-ta')).toBe('text');
  expect(await select(page, '#probe-pre')).toBe('text');
});
