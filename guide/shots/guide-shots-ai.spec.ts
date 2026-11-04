// The screenshots of the AI assistant page that need a running assistant: the buttons of an ended session. The plugin settings with the GPU memory line are in the same file.
// Codex is the stand-in of the web tests (web/tests/fixtures/fake-codex.js), so nothing here needs an account.
import * as fs from 'node:fs';
import * as path from 'node:path';
import { expect, test } from '@playwright/test';
import { open, shot } from './lib';

test.use({ viewport: { width: 1500, height: 900 }, deviceScaleFactor: 1 });
test.setTimeout(240000);

const SONG = `composition "band" { ample 1  tempo 96  master-seed 42 }
midi-clip chords stable@1.0 at 1 for 8 {
  chords Am F C G
}
`;

test('the AI session that ended, and the plugin settings with the GPU memory', async ({ page }) => {
  process.env.AMPLE_AI_SESSION_IDLE_S = '3';   // the real time is half an hour
  const server = await open(page, SONG);
  try {
    fs.writeFileSync(path.join(server.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
    const call = (method: string, url: string, body?: object) => page.evaluate(async ([m, u, b]) => {
      const token = sessionStorage.getItem('ample.token') || '';
      const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json', 'X-Ample-Token': token }, body: b ? JSON.stringify(b) : undefined });
      return r.json();
    }, [method, url, body ?? null]);
    await call('POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
    const task = await call('POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Write a short comment above each block.' });
    await expect.poll(async () => (await call('GET', `/api/ai/tasks/${task.id}`)).sessionEnded, { timeout: 90000 }).toBe(true);
    await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
    await expect(page.locator('#ai-session-ended')).toBeVisible({ timeout: 30000 });
    await page.waitForTimeout(500);
    const row = (await page.locator('#ai-session-ended').boundingBox())!;
    const dlg = (await page.locator('#ai-task').boundingBox())!;
    await shot(page, 'ai-session-ended', { x: dlg.x, y: row.y - 24, width: dlg.width, height: dlg.y + dlg.height - (row.y - 24) });
    await call('POST', `/api/ai/tasks/${task.id}/reject`, {});
    await page.evaluate(() => document.querySelectorAll('dialog[open]').forEach((d) => (d as HTMLDialogElement).close()));

    await page.evaluate(() => import('/app/pluginui.js').then((m) => m.openPluginSettings('voice-clone')));
    await expect(page.locator('#pg-gpu')).toBeVisible({ timeout: 30000 });
    await page.waitForTimeout(600);
    const gpu = (await page.locator('#pg-gpu').boundingBox())!;
    await shot(page, 'plugin-gpu-memory', { x: gpu.x - 20, y: gpu.y - 150, width: 720, height: 210 });
  } finally {
    delete process.env.AMPLE_AI_SESSION_IDLE_S;
    await server.stop();
  }
});
