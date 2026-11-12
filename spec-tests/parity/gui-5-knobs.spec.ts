import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// GUI flow 5: the value of a knob is shown next to it while it is dragged, and is gone when the knob is let go.
freshServer();
test('a dragged knob shows its value in a bubble, which goes away when it is let go', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'delay', 'k-delay');
  await node(page, 'k-delay').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const knob = page.locator('dialog.studio-floating[open]').getByRole('slider', { name: 'Time' });
  await expect(knob).toBeVisible();
  const box = (await knob.boundingBox())!;
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width / 2 + 30, box.y + box.height / 2, { steps: 5 });
  const bubble = page.locator('.studio-knob-bubble');
  await expect(bubble).toBeVisible();
  await expect.poll(async () => (await bubble.textContent()) === (await page.locator('dialog.studio-floating[open] .studio-knob-value').first().textContent())).toBe(true);
  await page.mouse.up();
  await expect(bubble).toHaveCount(0);
});
