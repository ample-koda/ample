import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';

// The AI assistant settings and the safety rules of the server policy. No Codex is started here.
freshServer();
test.beforeEach(async ({ page }) => { await openApp(page); });

const openSettings = async (page) => {
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiSettings()));
  await expect(page.locator('#ai-status')).toBeVisible();
};

test('a project can choose an assistant and a mode, and the choice is kept', async ({ page }) => {
  await openSettings(page);
  await page.evaluate(() => (document.querySelector('input[name="ai-assistant"][value="codex-server"]') as HTMLElement).click());
  await page.locator('#ai-risk-check').check();   // choosing an assistant asks to acknowledge what it may do first
  await page.click('#ai-risk-go');
  await expect.poll(async () => (await page.evaluate(() => fetch('/api/ai/status', { headers: { 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' } }).then((r) => r.json()))).project.assistant).toBe('codex-server');
});

test('the policy can be tightened from the page but never widened, and a switched-off switch cannot be switched on again there', async ({ page }) => {
  await openSettings(page);
  const call = (body: object) => page.evaluate(async (b) => { const r = await fetch('/api/ai/policy', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' }, body: JSON.stringify(b) }); return { status: r.status, body: await r.json() }; }, body);
  const off = await call({ allowUnsafe: false });
  expect(off.status).toBe(200);
  const widen = await call({ allowUnsafe: true });
  expect(widen.status).toBeGreaterThanOrEqual(400);
  expect(JSON.stringify(widen.body)).toContain('AMP5459');
  const program = await call({ codexPath: 'C:/anything/else.exe' });
  expect(JSON.stringify(program.body)).toContain('AMP5459');
  await page.evaluate(() => document.querySelectorAll('dialog[open]').forEach((d) => (d as HTMLDialogElement).close()));
  await openSettings(page);
  await page.locator('#ai-policy summary').click();
  await expect(page.locator('#ai-policy input[data-policy="allowUnsafe"]')).toBeDisabled();
});

test('the AI menu of the source editor leads to the settings while no assistant is chosen', async ({ page }) => {
  await page.evaluate(() => fetch('/api/ai/settings', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' }, body: JSON.stringify({ assistant: 'none' }) }));
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]'); await expect(page.locator('.node').first()).toBeVisible();
  await page.click('#vs-blocks');
  await page.click('#vs-source');
  await page.locator('#se-cm .cm-content').click();
  await page.locator('#se-ai').click();
  // no assistant is chosen yet: the menu leads to the settings instead of asking Codex
  await expect(page.locator('#menu div', { hasText: 'Set up the Assistant' }).first()).toBeVisible();
  await page.locator('#menu div', { hasText: 'Set up the Assistant' }).first().click();
  await expect(page.locator('#ai-status')).toBeVisible();
});

test('the model of the server can be chosen (a fast and affordable one is the default) and handed back to the default of Codex', async ({ page }) => {
  const call = (body?: object) => page.evaluate(async (b) => { const r = await fetch('/api/ai/policy', { method: b ? 'POST' : 'GET', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': sessionStorage.getItem('ample.token') || '' }, body: b ? JSON.stringify(b) : undefined }); return r.json(); }, body ?? null);
  expect((await call()).model).toBe('gpt-6-luna');
  expect((await call({ model: 'gpt-6-sol', effort: 'high' })).model).toBe('gpt-6-sol');
  expect((await call()).effort).toBe('high');
  expect((await call({ model: 'default', effort: 'default' })).model).toBe('default');
  await page.evaluate(() => import('/app/ai.js').then((m) => m.openAiSettings()));
  await page.locator('#ai-policy summary').click();
  await expect(page.locator('#ai-model')).toBeVisible();
});
