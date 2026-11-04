import { test, expect } from '@playwright/test';
import { api, chooseMenu, freshServer, node, openApp, state } from '../../web/tests/support';

freshServer();

test('baking keeps a take with its complete plan and the source says baked; unbake removes only the word and the take stays recoverable', async ({ page }) => {
  await openApp(page);
  await page.click('#btn-build');
  await expect(page.locator('#btxt')).toHaveText('idle');

  await node(page, 'master').click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Bake (freeze the result into the project)');
  await expect.poll(async () => (await state(page)).text).toMatch(/master master bake \{/);
  await expect(node(page, 'master').locator('.tag.bake')).toBeVisible();

  const takes = await api('/api/takes');
  expect(takes.length).toBeGreaterThan(0);
  for (const t of takes) {
    const read = await api(`/api/takes/${t.takeId}`);
    expect(JSON.stringify(read.plan)).toContain('ample-plan/1');
    expect(read.missing).toEqual([]);
  }

  await node(page, 'master').click({ button: 'right' });
  await chooseMenu(page, 'Edit', 'Unbake (back to recipe only)');
  await expect.poll(async () => (await state(page)).text).toMatch(/master master \{/);
  expect((await api('/api/takes')).length).toBe(takes.length);
});
