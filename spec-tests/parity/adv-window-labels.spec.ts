import { test, expect } from '@playwright/test';
import { addTemplateBlock, freshServer, node, openApp } from '../../web/tests/support';

// One label per knob, the live state of a parameter is a dot with a tooltip, and the explanations are behind a collapsed (i).
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
});
const open = async (page, type: string) => {
  await addTemplateBlock(page, type, `l-${type}`);
  await node(page, `l-${type}`).getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  const w = page.locator('#studio[open]');
  await expect(w).toBeVisible();
  return w;
};

test('a knob has one visible name (under the knob), not the same name above and below', async ({ page }) => {
  const w = await open(page, 'compressor');
  const row = w.locator('.studio-panel-control', { has: page.getByRole('slider', { name: 'Threshold' }) });
  await expect(row.locator('.studio-knob-label')).toBeVisible();
  await expect(row.locator('> span:first-child')).toBeHidden();
  const visible = await row.evaluate((el) => [...el.querySelectorAll('span')].filter((s) => (s as HTMLElement).offsetParent && /threshold/i.test(s.textContent || '')).length);
  expect(visible).toBe(1);
});

test('the live state of a knob is a small dot with a tooltip, not text', async ({ page }) => {
  const w = await open(page, 'reverb');
  const dot = w.locator('.studio-panel-control > .live-badge').first();
  await expect(dot).toHaveAttribute('title', /./);
  const box = (await dot.boundingBox())!;
  expect(box.width).toBeLessThanOrEqual(10);
});

test('explanations are behind a collapsed (i) that opens on click', async ({ page }) => {
  const w = await open(page, 'compressor');
  const info = w.locator('details.studio-info');
  await expect(info).toHaveCount(1);
  await expect(info).not.toHaveAttribute('open', '');
  await expect(info.getByText(/Sidechain:/)).toBeHidden();
  await info.locator('summary').click();
  await expect(info.getByText(/Sidechain:/)).toBeVisible();
  await expect(info).toContainText(/Live while you move them|These need a build to be heard/);
});
