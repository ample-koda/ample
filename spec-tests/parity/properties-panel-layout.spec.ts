import { test, expect } from '@playwright/test';
import { freshServer, openApp, node } from '../../web/tests/support';

freshServer();

test('node properties open at the top without horizontal overflow', async ({ page }) => {
  await page.route('**/api/sound-check?**', (route) => route.fulfill({ json: { ok: true, master: null } }));
  await page.setViewportSize({ width: 920, height: 720 });
  await openApp(page);
  const panel = page.locator('#right');
  await panel.evaluate((element) => { element.querySelector<HTMLElement>('#props')!.style.minHeight = '1400px'; element.scrollTop = 400; });
  await expect.poll(() => panel.evaluate((element) => element.scrollTop)).toBe(400);
  await node(page, 'chords').locator('.studio-advanced-node').click();
  await expect.poll(() => panel.evaluate((element) => element.scrollTop)).toBe(0);
  await expect.poll(() => panel.evaluate((element) => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
});

test('a late update in hidden Info does not move the properties actions', async ({ page }) => {
  await page.setViewportSize({ width: 1200, height: 900 });
  const message = 'Synthetic level check detail. '.repeat(80);
  await openApp(page);
  await node(page, 'master').locator('.node-nameplate').click();
  await page.getByRole('tab', { name: 'Parameters', exact: true }).click();
  const { before, after } = await page.evaluate((text) => {
    const actions = document.querySelector('.prop-actions')!;
    const before = actions.getBoundingClientRect().top;
    document.querySelector('.master-meter-text')!.textContent = text;
    return { before, after: actions.getBoundingClientRect().top };
  }, message);
  expect(Math.abs(after - before)).toBeLessThanOrEqual(1);
  const gap = await page.evaluate(() => (document.querySelector('.prop-management') ?? document.querySelector('.prop-actions'))!.getBoundingClientRect().top - document.querySelector('.prop-tabpanel.active')!.getBoundingClientRect().bottom);
  expect(gap).toBeLessThanOrEqual(20);
});
