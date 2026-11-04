import { test, expect } from '@playwright/test';
import { node, openApp, state } from '../../web/tests/support';

// Not a sketch feature id (so it is not in the parity matrix): the plan for chunk 75 asks for keyboard-only use of the canvas.
test.beforeEach(async ({ page }) => { await openApp(page); });

test('blocks can be reached with Tab, moved with the arrow keys and removed with Delete', async ({ page }) => {
  await node(page, 'chords').focus();
  expect((await state(page)).sel).toEqual(['chords']);
  await page.keyboard.press('ArrowRight');
  await expect.poll(async () => (await state(page)).text).toMatch(/layout chords \d+ \d+/);
  await page.keyboard.press('Tab');
  for (let i = 0; i < 4 && (await page.locator('.studio-advanced-node:focus').count()) === 0; i++) await page.keyboard.press('Tab');   // the status button comes first
  await expect(page.locator('.studio-advanced-node:focus')).toBeFocused();
  for (let i = 0; i < 8 && (await page.locator('.node:focus').count()) === 0; i++) await page.keyboard.press('Tab');   // the block's own buttons come first
  await expect(page.locator('.node:focus')).toHaveCount(1);
  expect((await state(page)).sel.length).toBe(1);
  await page.keyboard.press('Control+z');
  await page.keyboard.press('Control+z');
});

test('Enter on a focused block shows its source, and Delete refuses a block that another block still reads', async ({ page }) => {
  await node(page, 'bass').focus();
  await page.keyboard.press('Enter');
  // Enter opens the Source view on the block's line; back in the node view the block is still there to delete
  await expect(page.locator('#se-cm .cm-content')).toBeVisible();
  await page.locator('#vs-blocks').click();
  await node(page, 'bass').focus();
  await page.keyboard.press('Delete');
  await expect(page.locator('#toast')).toContainText('still reads');
  await expect(node(page, 'bass')).toHaveCount(1);
});
