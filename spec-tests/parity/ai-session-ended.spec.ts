import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, ownServer } from '../../web/tests/support';

// The Codex session of a finished proposal ends after a while without a message (shortened here with AMPLE_AI_SESSION_IDLE_S).
// The owner then chooses: resume the same thread, or start a new session in the same working copy.
// The variable goes to the server of this file only: on process.env of the run it leaked into the servers of the other files (ai-proposal-preview lost its session after 3 s).
freshServer({ env: { AMPLE_AI_SESSION_IDLE_S: '3' } });

const makeTask = async (page) => {
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await api('/api/ai/settings', { method: 'POST', body: { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true } });
  const task = await api('/api/ai/tasks', { method: 'POST', body: { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' } });
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).build?.status, { timeout: 120000 }).toBe('ok');
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).sessionEnded, { timeout: 30000 }).toBe(true);
  return task;
};

test('an ended session offers to resume the conversation, and the proposal stays', async ({ page }) => {
  test.setTimeout(240000);
  const task = await makeTask(page);
  const ended = await api(`/api/ai/tasks/${task.id}`);
  expect(ended.canMessage).toBe(false);
  expect(ended.canResumeThread).toBe(true);
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-session-ended')).toBeVisible({ timeout: 30000 });
  await page.locator('#ai-session-resume').click();
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).canMessage, { timeout: 30000 }).toBe(true);
  const back = await api(`/api/ai/tasks/${task.id}`);
  expect(back.status).toBe('ready');
  expect(back.sessionEnded).toBe(false);
  await expect.poll(async () => ((await api(`/api/ai/tasks/${task.id}`)).log as { text: string }[]).some((l) => /resumed/i.test(l.text)), { timeout: 15000 }).toBe(true);   // the note comes a moment after the session is back
  await api(`/api/ai/tasks/${task.id}/reject`, { method: 'POST', body: {} });
});

test('an ended session can start a new session that is told what was asked and what is changed', async ({ page }) => {
  test.setTimeout(240000);
  const task = await makeTask(page);
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-session-new')).toBeVisible({ timeout: 30000 });
  await page.locator('#ai-session-new').click();
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).canMessage, { timeout: 30000 }).toBe(true);
  const back = await api(`/api/ai/tasks/${task.id}`);
  expect(back.status).toBe('ready');
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).log.some((l) => /new Codex session started/i.test(l.text))).toBe(true);
  await api(`/api/ai/tasks/${task.id}/reject`, { method: 'POST', body: {} });
});
