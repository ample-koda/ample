import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, bottomTab, freshServerPerTest, openApp, ownServer } from '../../web/tests/support';

// UI round 1: destructive actions ask first (the ask() dialog). Cancelling keeps the thing, confirming removes it. DOM assertions only.
freshServerPerTest();
test.use({ permissions: ['microphone'], launchOptions: { args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] } });

const buttons = (page) => page.locator('#dlg-btns button');
const answer = async (page, label: string) => { await expect(page.locator('#dlg')).toBeVisible(); await buttons(page).filter({ hasText: new RegExp(`^${label}$`) }).click(); await expect(page.locator('#dlg')).not.toBeVisible(); };

test('AI: rejecting a proposal and deleting a conversation both ask first', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await api('/api/ai/settings', { method: 'POST', body: { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true } });
  const task = await api('/api/ai/tasks', { method: 'POST', body: { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' } });
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-reject')).toBeEnabled({ timeout: 60000 });

  // reject: Keep leaves the proposal open and ready, Reject closes it
  await page.click('#ai-reject');
  await expect(buttons(page)).toHaveText(['Reject', 'Keep']);
  await answer(page, 'Keep');
  await expect(page.locator('#ai-task')).toBeVisible();
  expect((await api(`/api/ai/tasks/${task.id}`)).status).toBe('ready');
  await page.click('#ai-reject');
  await answer(page, 'Reject');
  await expect(page.locator('#ai-task')).not.toBeVisible();
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).status).toBe('rejected');

  // conversation delete: Cancel keeps the card, Delete removes it
  await bottomTab(page, 'assistant');
  await page.getByRole('tab', { name: /Archived/ }).click();   // a rejected proposal's conversation is archived
  const cards = page.locator('#b-assistant .assistant-session-card');
  await expect(cards).toHaveCount(1);
  await page.locator('#b-assistant .assistant-session-action.danger').first().click();
  await expect(buttons(page)).toHaveText(['Delete', 'Cancel']);
  await answer(page, 'Cancel');
  await expect(cards).toHaveCount(1);
  await page.locator('#b-assistant .assistant-session-action.danger').first().click();
  await answer(page, 'Delete');
  await expect(cards).toHaveCount(0);
});

test('finals: removing a final asks first', async ({ page }) => {
  await openApp(page);
  await page.evaluate(() => import('/app/store.js').then((m) => { m.S.path = 'song.amplet'; }));
  await api('/api/finals', { method: 'POST', body: { song: 'song.amplet', name: 'teaser', fromBar: 1, bars: 2, format: 'wav' } });
  await page.evaluate(() => import('/app/projects.js').then((m) => m.openProjects('finals')));
  const row = page.locator('#fin-list [data-final="teaser"]');
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Remove' }).click();
  await expect(buttons(page)).toHaveText(['Remove', 'Cancel']);
  await answer(page, 'Cancel');
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Remove' }).click();
  await answer(page, 'Remove');
  await expect(page.locator('#fin-list [data-final="teaser"]')).toHaveCount(0);
});

test('recorder: discarding a take and closing with unused takes both ask first', async ({ page }) => {
  await openApp(page);
  await page.getByRole('button', { name: 'Record', exact: true }).click();
  const dialog = page.locator('dialog.recorder');
  await expect(dialog).toBeVisible();
  await dialog.locator('#rec-count').uncheck();
  for (let i = 0; i < 2; i++) {
    await dialog.getByRole('button', { name: /Record$/ }).click();
    await expect(dialog.getByRole('button', { name: /Stop/ })).toBeEnabled({ timeout: 15000 });
    await page.waitForTimeout(800);
    await dialog.getByRole('button', { name: /Stop/ }).click();
    await expect(dialog.locator('.rec-take')).toHaveCount(i + 1, { timeout: 15000 });
  }
  // discard take: Keep leaves two, Discard leaves one
  await dialog.locator('button[title="Discard this take"]').first().click();
  await expect(buttons(page)).toHaveText(['Discard', 'Keep']);
  await answer(page, 'Keep');
  await expect(dialog.locator('.rec-take')).toHaveCount(2);
  await dialog.locator('button[title="Discard this take"]').first().click();
  await answer(page, 'Discard');
  await expect(dialog.locator('.rec-take')).toHaveCount(1);
  // close with an unused take: Keep recording keeps the recorder, Close removes it
  await page.keyboard.press('Escape');
  await expect(buttons(page)).toHaveText(['Close', 'Keep recording']);
  await answer(page, 'Keep recording');
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await answer(page, 'Close');
  await expect(page.locator('dialog.recorder')).toHaveCount(0);
});
