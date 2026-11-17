import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// Plan local-preview 8.3: a knob is a keyboard-usable ARIA slider whose spoken value carries the unit.
freshServer();
test('@audio a knob moves by keyboard (arrow = fine, PageUp/PageDown = coarse, Home/End = ends) and speaks its unit', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'delay', 'k-delay');
  await node(page, 'k-delay').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const knob = page.locator('dialog.studio-floating[open]').getByRole('slider', { name: 'Time' });
  await expect(knob).toBeVisible();
  const now = async () => Number(await knob.getAttribute('aria-valuenow'));
  await knob.focus();
  await expect(knob).toHaveAttribute('aria-valuetext', /\d\s*ms/);
  // Every key press commits the value and the panel redraws from the song a moment later; a redraw of an EARLIER commit can still land after the next key press
  // and show the old value for a moment. So each step waits for its value (auto-retrying) before the next key goes, and the key that follows always starts from a settled knob.
  await knob.press('Home');
  const min = Number(await knob.getAttribute('aria-valuemin'));
  await expect.poll(now).toBe(min);
  await knob.press('ArrowUp');
  await expect.poll(now).toBeGreaterThan(min);
  const fine = (await now()) - min;
  await knob.press('Home');
  await expect.poll(now).toBe(min);
  await knob.press('PageUp');
  await expect.poll(async () => (await now()) - min).toBeGreaterThan(fine * 3);
  await knob.press('PageDown');
  await expect.poll(now).toBe(min);
  await knob.press('End');
  await expect.poll(async () => now()).toBe(Number(await knob.getAttribute('aria-valuemax')));
});
