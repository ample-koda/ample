import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

freshServer();

test('theme selection updates the document attribute and token-driven surfaces', async ({ page }) => {
  await openApp(page, { reset: false });
  const before = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim());
  const light = await page.evaluate(async () => {
    const ui = await import('/app/ui.js');
    ui.setTheme('light');
    return { theme: document.documentElement.dataset.theme, background: getComputedStyle(document.documentElement).getPropertyValue('--bg').trim(), stored: localStorage.getItem('ample.theme') };
  });
  expect(light).toEqual({ theme: 'light', background: '#f3f4f6', stored: 'light' });
  expect(light.background).not.toBe(before);
  await page.evaluate(async () => { const ui = await import('/app/ui.js'); ui.setTheme('studio-dark'); });
});

test('studio gallery exposes accessible controls and linear knob dragging raises its value', async ({ page }) => {
  await openApp(page, { reset: false });
  await page.evaluate(() => { history.replaceState(null, '', '/?gallery=1'); import('/app/studio/index.js').then((module) => module.mountStudioGallery()); });
  const gallery = page.locator('#studio-gallery');
  await expect(gallery).toBeVisible();
  const knob = gallery.getByRole('slider', { name: 'Cutoff' });
  const before = Number(await knob.getAttribute('aria-valuenow'));
  const bounds = await knob.boundingBox();
  if (!bounds) throw new Error('The studio knob is not visible.');
  await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
  await page.mouse.down();
  await page.mouse.move(bounds.x + bounds.width / 2 + 20, bounds.y + bounds.height / 2);
  await page.mouse.up();
  expect(Number(await knob.getAttribute('aria-valuenow'))).toBeGreaterThan(before);
  await expect(gallery.getByRole('switch', { name: 'Enabled' })).toHaveAttribute('aria-checked', 'true');
  const meter = gallery.getByRole('meter', { name: 'Output meter' });
  await expect(meter).toBeVisible();
  await meter.click();
  await expect(meter).not.toHaveAttribute('aria-valuenow', '0.62');
  const scrub = gallery.getByRole('slider', { name: 'Preview scrub' });
  const scrubBox = await scrub.boundingBox();
  if (!scrubBox) throw new Error('The scrub bar is not visible.');
  await page.mouse.move(scrubBox.x + 20, scrubBox.y + scrubBox.height / 2);
  await page.keyboard.down('Shift');
  await page.mouse.down();
  await page.mouse.move(scrubBox.x + scrubBox.width * .6, scrubBox.y + scrubBox.height / 2);
  await page.mouse.up();
  await page.keyboard.up('Shift');
  await expect(gallery.locator('output.note')).toContainText('Loop set:');
  const curve = gallery.locator('.studio-curve svg');
  await expect(curve.locator('.studio-curve-point')).toHaveCount(4);
  await curve.dblclick({ position: { x: 120, y: 70 } });
  await expect(curve.locator('.studio-curve-point')).toHaveCount(5);
  const inserted = curve.locator('.studio-curve-point').nth(2);
  const previous = Number(await inserted.getAttribute('aria-valuenow'));
  await inserted.focus(); await inserted.press('ArrowUp');
  await expect(inserted).not.toHaveAttribute('aria-valuenow', String(previous));
  await inserted.press('Delete');
  await expect(curve.locator('.studio-curve-point')).toHaveCount(4);
});
