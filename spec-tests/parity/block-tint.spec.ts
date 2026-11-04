import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

// A block can be tinted from the Properties panel (any block type); the tint is in the source and does not change the sound.
test('the tint dots in Properties colour the card and write the tint parameter; the same dot takes it away', async ({ page }) => {
  await openApp(page);
  const node = page.locator('.node').first();
  await node.click();
  const dot = page.getByRole('radio', { name: 'purple' });
  await dot.click();
  await expect(page.locator('.node.tinted').first()).toHaveAttribute('data-tint', 'purple');
  await page.locator('#vs-source').click();
  await expect(page.locator('#se')).toContainText('tint purple');
  await page.locator('#vs-blocks').click();
  await page.locator('.node').first().click();
  await page.getByRole('radio', { name: 'purple' }).click();
  await expect(page.locator('.node.tinted')).toHaveCount(0);
});
