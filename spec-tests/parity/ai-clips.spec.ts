import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

// The clips Codex presents, driven through the real task-scoped present route by the stand-in (web/tests/fixtures/fake-codex.js: PRESENT_<n>, TRIAL_<n>,
// ECHO_PROMPT in the prompt). Covers the quantity limit, continuation, trial edits and restoring the task after a reload.
freshServer();

const token = (page) => page.evaluate(() => sessionStorage.getItem('ample.token') || '');
const call = async (page, method: string, url: string, body?: object) => page.evaluate(async ([m, u, b, t]) => {
  const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json', 'X-Ample-Token': t as string }, body: b ? JSON.stringify(b) : undefined });
  return r.json();
}, [method, url, body ?? null, await token(page)]);

test.beforeEach(async ({ page }) => {
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
});

const start = (page, prompt: string) => call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt });
const ready = (page, id: string, turns: number) => expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${id}`); return t.status === 'ready' ? (t.log as any[]).filter((l) => l.kind === 'assistant' || l.kind === 'agent').length : -1; }, { timeout: 120000 }).toBeGreaterThanOrEqual(turns);
const answers = async (page, id: string) => ((await call(page, 'GET', `/api/ai/tasks/${id}`)).log as any[]).filter((l) => l.kind === 'assistant').map((l) => String(l.text)).filter((t) => t.includes('PRESENTED'));
const continuationPrompt = async (page, id: string) => ((await call(page, 'GET', `/api/ai/tasks/${id}`)).log as any[]).map((l) => String(l.text)).filter((t) => t.startsWith('Continue ONLY work'));
const proposalClips = async (page, id: string) => ((await call(page, 'GET', `/api/ai/tasks/${id}`)).clips as any[]).filter((c) => c.source === 'proposal');

const parse = (text: string) => JSON.parse(text.slice(text.indexOf('PRESENTED ') + 'PRESENTED '.length, text.indexOf(' PROMPT_BEGIN') > 0 ? text.indexOf(' PROMPT_BEGIN') : undefined)) as any[];

test('a five sample request stops at five clips over a continuation, keeps what was asked, and a new explicit request starts again', async ({ page }) => {
  test.setTimeout(240000);
  const task = await start(page, 'Give me five samples. PRESENT_3 ECHO_PROMPT');
  await ready(page, task.id, 1);
  expect((await proposalClips(page, task.id)).length).toBe(3);

  // Continue: two more fit, the rest is refused by the limit of five
  const cont = await call(page, 'POST', `/api/ai/tasks/${task.id}/continue`, { message: 'Carry on with the remaining ones. PRESENT_4 ECHO_PROMPT' });
  expect(cont.started).not.toBe(false);
  await ready(page, task.id, 2);
  const second = parse((await answers(page, task.id))[1]);
  // (second turn results)
  expect(second.filter((r) => r.presented === true).length).toBe(2);
  expect(second.filter((r) => r.limitReached === true).length).toBe(2);
  expect((await proposalClips(page, task.id)).length).toBe(5);

  // the continuation prompt kept the original request and said what was already delivered
  const echoed = (await continuationPrompt(page, task.id))[0] ?? '';
  expect(echoed).toContain('Give me five samples');
  expect(echoed).toContain('"sampleLimit":5');
  expect(echoed).toContain('"remainingSamples":2');
  expect((echoed.match(/"label":"fake clip/g) || []).length).toBe(3);   // the clips delivered so far travel with it

  // nothing left to continue
  const again = await call(page, 'POST', `/api/ai/tasks/${task.id}/continue`, { message: 'Once more' });
  expect(again.started).toBe(false);
  expect(again.limitReached).toBe(true);

  // a new explicit request is a new quantity: the limit does not follow it
  await call(page, 'POST', `/api/ai/tasks/${task.id}/message`, { text: 'Give me two more samples. PRESENT_1' });
  await ready(page, task.id, 3);
  expect((await proposalClips(page, task.id)).length).toBe(6);
});

test('a trial edit gives other audio than the same clip without it, and an identical clip is reported as identical', async ({ page }) => {
  test.setTimeout(240000);
  const task = await start(page, 'Compare the bass. PRESENT_1');
  await ready(page, task.id, 1);
  const base = parse((await answers(page, task.id))[0])[0];
  expect(base.hash).toBeTruthy();
  expect(base.identicalTo).toBeNull();

  await call(page, 'POST', `/api/ai/tasks/${task.id}/message`, { text: 'Try it quieter. PRESENT_1 TRIAL_-12' });
  await ready(page, task.id, 2);
  const quieter = parse((await answers(page, task.id))[1])[0];
  expect(quieter.hash).not.toBe(base.hash);
  expect(quieter.identicalTo).toBeNull();

  await call(page, 'POST', `/api/ai/tasks/${task.id}/message`, { text: 'The same again. PRESENT_1 TRIAL_-12' });
  await ready(page, task.id, 3);
  const same = parse((await answers(page, task.id))[2])[0];
  expect(same.hash).toBe(quieter.hash);
  expect(same.identicalTo).toBeTruthy();   // the truth: no audible difference to the earlier clip
});

test('after a reload the task and its clips are back and no extra Codex turn starts', async ({ page }) => {
  test.setTimeout(240000);
  const task = await start(page, 'Two samples please. PRESENT_2');
  await ready(page, task.id, 1);
  const before = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await page.waitForTimeout(3000);
  const after = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(after.status).toBe('ready');
  expect((after.clips as any[]).map((c) => c.id)).toEqual((before.clips as any[]).map((c) => c.id));
  expect((after.log as any[]).filter((l) => l.kind === 'assistant').length).toBe((before.log as any[]).filter((l) => l.kind === 'assistant').length);
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-task')).toBeVisible();
});
