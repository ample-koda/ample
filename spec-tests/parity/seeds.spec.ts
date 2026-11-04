import { test, expect } from '@playwright/test';
import { api, dialog, freshServer, node, openApp, openTab } from '../../web/tests/support';

freshServer();
// The seeds live on the server, so every test starts by locking them again.
test.beforeEach(async ({ page }) => {
  await api('/api/control/seed', { method: 'POST', body: { path: 'song.amplet', block: 'master', name: 'seed', action: 'lock' } });
  await openApp(page);
  await node(page, 'master').locator('.nh .t').click();
});

const seed = async () => (await api('/api/control/block?path=song.amplet&block=master')).seeds[0];
const setSeed = (value: string) => api('/api/control/seed', { method: 'POST', body: { path: 'song.amplet', block: 'master', name: 'seed', action: 'set', value } });

test('seeds are locked by default and cannot be typed into', async ({ page }) => {
  await expect(page.locator('[data-controls="master"]')).toContainText('locked');
  await expect(page.locator('[data-seed="seed"]')).toBeDisabled();
  expect((await seed()).state.locked).toBe(true);
});

test('unlocking explains what it breaks, then a seed can be typed and the old one is kept', async ({ page }) => {
  await openTab(page, 'info');
  await page.locator('[data-controls="master"] button', { hasText: 'Unlock to edit' }).click();
  await expect(page.locator('#dlg-b')).toContainText('can no longer be reproduced');
  await dialog(page, 'Unlock');
  await expect(page.locator('[data-seed="seed"]')).toBeEnabled();
  const old = (await seed()).state.value;
  const input = page.locator('[data-seed="seed"]');
  await input.fill('12345');
  await input.press('Tab');
  await expect.poll(async () => (await seed()).state.value).toBe('12345');
  await expect(page.locator('#toast')).toContainText('reproduced');
  await expect(page.locator('[data-controls="master"]')).toContainText(`${old} → 12345`);
});

test('randomize gives another seed and locking makes it fixed again', async ({ page }) => {
  await openTab(page, 'info');
  await page.locator('[data-controls="master"] button', { hasText: 'Unlock to edit' }).click();
  await dialog(page, 'Unlock');
  const before = (await seed()).state.value;
  await page.locator('[data-controls="master"] button', { hasText: '🎲' }).click();
  await expect.poll(async () => (await seed()).state.value).not.toBe(before);
  await page.locator('[data-controls="master"] button', { hasText: 'Lock the seeds' }).click();
  await expect.poll(async () => (await seed()).state.locked).toBe(true);
});

test('a seed that is a 64-bit number is shown exactly', async ({ page }) => {
  await api('/api/control/seed', { method: 'POST', body: { path: 'song.amplet', block: 'master', name: 'seed', action: 'unlock' } });
  await setSeed('18446744073709551615');
  await api('/api/control/seed', { method: 'POST', body: { path: 'song.amplet', block: 'master', name: 'seed', action: 'lock' } });
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await node(page, 'master').locator('.nh .t').click();
  await expect(page.locator('[data-seed="seed"]')).toHaveValue('18446744073709551615');
});
