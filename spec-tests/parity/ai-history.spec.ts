import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

// The memory of the AI work of a project: requests are kept (also on disk), listed, and a new request can continue an earlier one (stand-in Codex).
freshServer();

test('earlier requests are remembered, listed and can be continued', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token') || '');
  const api = (method: string, url: string, body?: unknown) => page.evaluate(async ([m, u, b, t]) => { const r = await fetch(u as string, { method: m as string, headers: { 'X-Ample-Token': t as string, 'Content-Type': 'application/json' }, body: b ? JSON.stringify(b) : undefined }); return r.json(); }, [method, url, body ?? null, token]);
  await api('POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const first = await api('POST', '/api/ai/tasks', { kind: 'custom', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Make the song slow down at the end, halving the speed every 2 bars.' });
  await expect.poll(async () => { const t = await api('GET', `/api/ai/tasks/${first.id}`); return t.status === 'ready' && ['ok', 'failed'].includes(t.build.status); }, { timeout: 200000 }).toBe(true);
  await api('POST', `/api/ai/tasks/${first.id}/reject`, {});

  const listed = await api('GET', '/api/ai/history?song=song.amplet');
  expect(listed.requests[0].id).toBe(first.id);
  expect(listed.requests[0].status).toBe('rejected');
  expect(listed.requests[0].prompt).toContain('slow down at the end');
  const file = path.join(ownServer()!.folder, '.ample', 'ai', 'history.jsonl');
  expect(fs.readFileSync(file, 'utf8')).toContain(first.id);   // kept on disk: it survives a restart of the server

  const second = await api('POST', '/api/ai/tasks', { kind: 'custom', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Not so strong a slowdown, and add the chorus.', continueFrom: first.id });
  await expect.poll(async () => JSON.stringify((await api('GET', `/api/ai/tasks/${second.id}`)).log), { timeout: 60000 }).toContain('Continues the earlier request');
  await expect.poll(async () => (await api('GET', `/api/ai/tasks/${second.id}`)).status, { timeout: 200000 }).toBe('ready');
  await api('POST', `/api/ai/tasks/${second.id}/reject`, {});
});

test('the AI prompt offers the earlier requests to continue', async ({ page }) => {
  test.setTimeout(120000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await page.evaluate((t) => fetch('/api/ai/settings', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': t }, body: JSON.stringify({ assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true }) }), await page.evaluate(() => sessionStorage.getItem('ample.token') || ''));
  await page.reload(); await page.waitForSelector('body[data-ready="1"]'); await expect(page.locator('.node').first()).toBeVisible();
  await page.click('#vs-blocks'); await page.click('#vs-source'); await page.locator('#se-cm .cm-content').click(); await page.locator('#se-ai').click();
  await page.locator('#menu div', { hasText: 'Generate' }).first().click();
  await expect(page.locator('#ai-earlier')).toBeVisible();
});
