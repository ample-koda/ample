import { test, expect } from '@playwright/test';
import { addTemplateBlock, api, freshServer, node, openApp } from '../../web/tests/support';

// The title bar of an Advanced window: one menu button (Snapshots, Reset to defaults, Help, Copy block code) and a live / build / off badge.
freshServer();
test.beforeEach(async ({ page }) => {
  await page.setViewportSize({ width: 1500, height: 950 });
  await openApp(page, { reset: false });
  await addTemplateBlock(page, 'compressor', 'm-comp');
  await node(page, 'm-comp').getByRole('button', { name: 'Advanced', exact: true }).evaluate((b: HTMLElement) => b.click());
  await expect(page.locator('#studio[open]')).toBeVisible();
});
const win = (page) => page.locator('#studio[open]');
const menu = async (page) => { await win(page).getByRole('button', { name: 'Menu', exact: true }).click(); return win(page).locator('.studio-menu'); };

test('the menu holds Snapshots, Reset to defaults, Help and Copy block code; the body has no snapshot widget', async ({ page }) => {
  await expect(win(page).locator('.studio-dialog-body .snapshot-bar')).toHaveCount(0);
  const m = await menu(page);
  for (const name of ['Snapshots', 'Reset to defaults', 'Help', 'Copy block code']) await expect(m.getByRole('menuitem', { name })).toBeVisible();
  await page.keyboard.press('Escape');   // closes only the menu
  await expect(m).toBeHidden();
  await expect(win(page)).toBeVisible();
});

test('Help opens the docs page of the block in a new tab', async ({ page }) => {
  const m = await menu(page);
  const popup = page.waitForEvent('popup');
  await m.getByRole('menuitem', { name: 'Help' }).click();
  const tab = await popup;
  expect(tab.url()).toContain('ample-koda.dev/#/blocks/compressor');
  await tab.close();
});

test('Reset to defaults puts the parameters back in one step', async ({ page }) => {
  const doc = await api('/api/doc?path=song.amplet');
  await api('/api/doc/edit', { method: 'POST', body: { path: 'song.amplet', baseVersion: doc.version, op: { type: 'setParameter', block: 'm-comp', name: 'ratio', value: '9' } } });
  const ratio = () => page.evaluate(() => import('/app/store.js').then((s) => s.flat().nodes.find((n) => n.id === 'm-comp')?.params?.ratio));
  await expect.poll(ratio).toBe('9');
  const m = await menu(page);
  await m.getByRole('menuitem', { name: 'Reset to defaults' }).click();
  await expect.poll(ratio).toBe('4');
});

test('Copy block code copies the block from the song source', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const m = await menu(page);
  await m.getByRole('menuitem', { name: 'Copy block code' }).click();
  const text = await page.evaluate(() => navigator.clipboard.readText());
  expect(text).toMatch(/^compressor m-comp/);
  expect(text.trim().endsWith('}') || !text.includes('{')).toBe(true);
});

test('the power switch and the status text are two things', async ({ page }) => {
  const power = win(page).getByRole('switch', { name: 'Effect enabled' }), status = win(page).locator('.studio-state-text');
  await expect(power).toHaveAttribute('aria-pressed', 'true');
  await expect(status).toHaveText('Live preview');
  await expect(status).toHaveAttribute('title', /heard at once/);
  await power.click();
  await expect(win(page).getByRole('switch', { name: 'Effect enabled' })).toHaveAttribute('aria-pressed', 'false');
  await expect(status).toHaveText('Bypassed');
});
