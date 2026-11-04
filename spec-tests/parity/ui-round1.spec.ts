import { test, expect } from '@playwright/test';
import { openApp } from '../../web/tests/support';

test.beforeEach(async ({ page }) => { await openApp(page); });

test('the toast is announced and sits in the top layer above an open dialog', async ({ page }) => {
  await page.evaluate(async () => {
    const { toast } = await import('/app/ui.js');
    (document.querySelector('#dlg') as HTMLDialogElement).showModal();
    toast('Something failed');
  });
  const toast = page.locator('#toast');
  await expect(toast).toHaveAttribute('aria-live', 'assertive');
  await expect(toast).toHaveAttribute('role', 'status');
  expect(await toast.evaluate((e) => e.matches(':popover-open'))).toBe(true);
});

test('Backspace in a text field does not delete canvas blocks', async ({ page }) => {
  const before = await page.locator('#cv .node').count();
  expect(before).toBeGreaterThan(0);
  await page.locator('#cv .node').first().click();
  await page.evaluate(() => { const i = document.createElement('input'); i.id = 'probe'; document.body.append(i); i.focus(); });
  await page.keyboard.press('Backspace');
  await page.keyboard.press('Delete');
  expect(await page.locator('#cv .node').count()).toBe(before);
});

test('clickable rows are keyboard operable', async ({ page }) => {
  const label = page.locator('.tlabel').first();
  if (await label.count()) { await expect(label).toHaveAttribute('role', 'button'); await expect(label).toHaveAttribute('tabindex', '0'); }
  const row = page.locator('.pf.click').first();
  await expect(row).toHaveAttribute('role', 'button');
});

test('the view switch stays inside a 390 px screen and scrolls instead of clipping', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  const box = (await page.locator('#vswitch').boundingBox())!;
  expect(box.x).toBeGreaterThanOrEqual(0);
  expect(box.x + box.width).toBeLessThanOrEqual(391);
  expect(await page.locator('#vswitch').evaluate((e) => getComputedStyle(e).overflowX)).toBe('auto');
});

test('at 390 px the header controls stay on screen and the bottom tab strip scrolls', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  for (const id of ['btn-projects', 'btn-save', 'btn-undo', 'btn-build', 'btn-stop', 'btn-help']) {
    const b = await page.locator('#' + id).boundingBox();
    if (!b) continue;
    expect(b.x, id).toBeGreaterThanOrEqual(0);
    expect(b.x + b.width, id).toBeLessThanOrEqual(391);
  }
  // the tabs either scroll (overflow auto) or, in the phone layout (MUI02), fit as a grid of fixed tabs
  expect(await page.locator('#btabs').evaluate((e) => getComputedStyle(e).overflowX === 'auto' || e.scrollWidth <= e.clientWidth + 1)).toBe(true);
  const m = await page.locator('#b-min').boundingBox();
  if (m) expect(m.x + m.width).toBeLessThanOrEqual(391);
  expect(await page.locator('#cv').evaluate((e) => getComputedStyle(e).touchAction)).toBe('none');
});
