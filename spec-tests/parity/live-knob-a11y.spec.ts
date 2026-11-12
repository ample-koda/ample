import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// Plan local-preview 8.3: a knob is a keyboard-usable ARIA slider whose spoken value carries the unit.
freshServer();
test('a knob moves by keyboard (arrow = fine, PageUp/PageDown = coarse, Home/End = ends) and speaks its unit', async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'delay', 'k-delay');
  await node(page, 'k-delay').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const knob = page.locator('dialog.studio-floating[open]').getByRole('slider', { name: 'Time' });
  await expect(knob).toBeVisible();
  const now = async () => Number(await knob.getAttribute('aria-valuenow'));
  await knob.focus();
  await expect(knob).toHaveAttribute('aria-valuetext', /\d\s*ms/);
  await knob.press('Home');
  const min = await now();
  await knob.press('ArrowUp');
  const fine = (await now()) - min;
  expect(fine).toBeGreaterThan(0);
  await knob.press('Home');
  await knob.press('PageUp');
  const coarse = (await now()) - min;
  expect(coarse).toBeGreaterThan(fine * 3);
  await knob.press('PageDown');
  expect(await now()).toBe(min);
  await knob.press('End');
  expect(await now()).toBe(Number(await knob.getAttribute('aria-valuemax')));
});
