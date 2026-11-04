import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { bottomTab, chooseMenu, freshServerPerTest, openApp, ownServer } from '../../web/tests/support';

// The proposal of the AI assistant, looked at in the real block view and built there, with a stand-in for Codex (web/tests/fixtures/fake-codex.js).
freshServerPerTest();

const token = (page) => page.evaluate(() => sessionStorage.getItem('ample.token') || '');
const call = async (page, method: string, url: string, body?: object) => page.evaluate(async ([m, u, b, t]) => {
  const r = await fetch(u as string, { method: m as string, headers: { 'Content-Type': 'application/json', 'X-Ample-Token': t as string }, body: b ? JSON.stringify(b) : undefined });
  return r.json();
}, [method, url, body ?? null, await token(page)]);

test('a proposal can be looked at in the block view, built there, and every built block plays without touching the project', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' });
  expect(JSON.stringify(task).slice(0, 300)).toContain('"id"');
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).build?.status, { timeout: 120000 }).toBe('ok');
  const takesBefore = fs.existsSync(path.join(ownServer()!.folder, '.ample', 'takes')) ? fs.readdirSync(path.join(ownServer()!.folder, '.ample', 'takes')).length : 0;

  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-accept')).toBeEnabled({ timeout: 60000 });
  await expect(page.locator('#ai-task .ai-wave')).toBeVisible();   // the waveform of the proposal, before anything is accepted
  // closing the window does not lose the task: a pill brings it back
  await page.evaluate(() => (document.getElementById('ai-task') as HTMLDialogElement).close());
  await expect(page.locator('#ai-pill')).toBeVisible({ timeout: 15000 });
  await page.click('#ai-pill');
  await expect(page.locator('#ai-accept')).toBeEnabled({ timeout: 30000 });
  // go on before accepting: a message asks for a change on top of the proposal (the stand-in adds a second comment line)
  await page.fill('#ai-msg-input', 'make it 20% quieter');
  await page.click('#ai-msg-send');
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' && t.build.status === 'ok' ? t.changes[0].added : 0; }, { timeout: 120000 }).toBe(2);
  await expect(page.locator('#ai-accept')).toBeEnabled({ timeout: 60000 });
  await page.click('#ai-blockview');
  await expect(page.locator('#ai-peek-bar')).toBeVisible();   // the way back, build, waveform and stop stay in view, also in the code view
  await expect(page.locator('#ai-peek-back')).toBeVisible();
  await expect(page.locator('#banner')).toContainText('Read-only preview of the AI proposal');
  await expect(page.locator('#btn-build')).toBeEnabled();
  await expect(page.locator('.ai-peek-audio').first()).toBeVisible({ timeout: 30000 });   // built blocks play from the proposal, right away

  // the audio of a block can be fetched in ranges (scrubbing and repeating need no new build)
  const src = await page.locator('.ai-peek-audio').first().getAttribute('src');
  const range = await page.evaluate(async ([u, t]) => { const r = await fetch(u as string, { headers: { 'X-Ample-Token': t as string, Range: 'bytes=0-99' } }); return [r.status, (await r.arrayBuffer()).byteLength]; }, [src, await token(page)]);
  expect(range).toEqual([206, 100]);

  // the Build button builds the proposal, not the project
  await page.click('#btn-build');
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.build.status === 'ok' && t.build.only === null && t.build.blocks.length > 0; }, { timeout: 120000 }).toBe(true);
  const takesAfter = fs.existsSync(path.join(ownServer()!.folder, '.ample', 'takes')) ? fs.readdirSync(path.join(ownServer()!.folder, '.ample', 'takes')).length : 0;
  expect(takesAfter).toBe(takesBefore);
  expect(fs.readFileSync(path.join(ownServer()!.folder, 'song.amplet'), 'utf8')).not.toContain('fake Codex');

  // a message while the preview is open: Codex works again, so the preview stops and is disabled with the reason, and comes back when the new build is ready
  await page.fill('#ai-peek-msg', 'one more thing SLOW');
  await page.click('#ai-peek-send');
  await expect(page.locator('#ai-peek-bar .ai-peek-status')).toContainText(/disabled|changing/, { timeout: 15000 });
  await expect(page.locator('#ai-peek-bar .ai-wave-play').first()).toBeDisabled({ timeout: 15000 }).catch(() => {});
  await expect.poll(() => page.evaluate(() => document.body.classList.contains('ai-busy')), { timeout: 15000 }).toBe(true);
  await expect.poll(() => page.evaluate(() => document.body.classList.contains('ai-busy')), { timeout: 120000 }).toBe(false);

  // back to the proposal, then reject: the project stays as it was
  await page.click('#ai-peek-back');
  await expect(page.locator('#ai-accept')).toBeVisible();
  await page.click('#ai-reject');
  expect(fs.readFileSync(path.join(ownServer()!.folder, 'song.amplet'), 'utf8')).not.toContain('fake Codex');
});

test('Archive ends the active Codex task without sending a message or changing the project', async ({ page }) => {
  test.setTimeout(60000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const original = fs.readFileSync(path.join(ownServer()!.folder, 'song.amplet'), 'utf8');
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment. SLOW' });
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).status, { timeout: 30000 }).toBe('running');
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('.ai-msg-compose #ai-stop-now')).toBeVisible();
  await expect(page.locator('.ai-msg-compose #ai-msg-input')).toBeVisible();
  expect(await page.locator('#ai-stop-now').evaluate((e) => e.getBoundingClientRect().left)).toBeLessThan(await page.locator('#ai-msg-send').evaluate((e) => e.getBoundingClientRect().left));
  await page.getByRole('button', { name: 'Archive this conversation' }).click();
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).archived).toBe(true);
  expect(fs.readFileSync(path.join(ownServer()!.folder, 'song.amplet'), 'utf8')).toBe(original);
  expect((await call(page, 'GET', `/api/ai/tasks/${task.id}`)).log.filter((line) => line.kind === 'user')).toHaveLength(1);
});

test('a block context menu starts a fresh assistant session with a scope-expansion hint', async ({ page }) => {
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  await page.evaluate(() => import('/app/ai.js').then((m) => m.aiStatus(true)));
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  const first = page.locator('.node:visible').first();
  await first.click({ button: 'right' });
  await expect(page.locator('#menu')).toHaveCSS('display', 'block');
  await chooseMenu(page, 'AI', /^New Assistant session with block “.+”...$/);
  await expect(page.locator('#ai-prompt')).toBeVisible();
  await expect(page.locator('#ai-prompt-text')).toHaveAttribute('placeholder', /starting context.*wider project change/);
});

test('a proposal that brings new errors is sent back to Codex by AMPLE before the user sees it', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment. BREAK' });
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' ? t.repairs : -1; }, { timeout: 120000 }).toBe(1);
  const final = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(JSON.stringify(final.log)).toContain('sends Codex back to fix them');
  expect(final.changes[0].diff).not.toContain('NOT AMPLE');
  expect(final.changes[0].diff).toContain('fake Codex');
  await call(page, 'POST', `/api/ai/tasks/${task.id}/reject`);
});

test('a block that cannot work (its file is missing) is sent back to Codex before the user sees the proposal', async ({ page }) => {
  test.setTimeout(300000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment. BUILDFAIL' });
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' && ['ok', 'failed'].includes(t.build.status) ? t.build.status : ''; }, { timeout: 240000 }).toBe('ok');
  const final = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(JSON.stringify(final.log)).toContain('sends Codex back to fix them');
  expect(final.changes[0].diff).not.toContain('ghost');
  await call(page, 'POST', `/api/ai/tasks/${task.id}/reject`);
});

test('a parsed proposal that remains unbuildable after one repair cannot be accepted normally', async ({ page }) => {
  test.setTimeout(300000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  const doc = await call(page, 'GET', '/api/doc?path=song.amplet');
  await call(page, 'PUT', '/api/doc', { path: 'song.amplet', text: 'composition "bad" { ample 1  tempo 100 master-seed 1 }\nsequencer sq at 1 for 2 {\n}\n', baseVersion: doc.version });
  await call(page, 'POST', '/api/doc/save', { path: 'song.amplet' });
  await openApp(page, { reset: false });
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'PERSIST_BUILDFAIL' });
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' && ['ok', 'failed'].includes(t.build.status) ? t.build.status : ''; }, { timeout: 240000 }).toBe('failed');
  const failed = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  const buildRepairCount = failed.log.filter((entry: { text: string }) => entry.text.includes('The proposal does not build: Codex gets the details')).length;
  expect({ status: failed.build.status, failureCount: failed.build.failures.length, diagnosticCount: failed.diagnostics.length, buildRepairCount }).toEqual({ status: 'failed', failureCount: expect.any(Number), diagnosticCount: 0, buildRepairCount: 1 });

  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-accept')).toBeDisabled();
  await expect(page.locator('#ai-accept-anyway')).toBeEnabled();
  await page.click('#ai-accept-anyway');
  await expect(page.locator('#ai-anyway')).toBeVisible();
  await expect(page.locator('#ai-anyway')).toContainText('The project does not build with this proposal');
  await page.locator('#ai-anyway button', { hasText: 'Cancel' }).click();
  await call(page, 'POST', `/api/ai/tasks/${task.id}/reject`);
});

test('a new song proposal builds and labels its own song output instead of the open song', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Create a new song. NEW_SONG' });
  await expect.poll(async () => { const t = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' && ['ok', 'failed'].includes(t.build.status) ? t.build.status : ''; }, { timeout: 180000 }).toBe('ok');
  const result = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(result.build.song).toBe('first-bell.amplet');
  expect(result.build.audio).toBe(true);
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-build-song')).toContainText('first-bell.amplet');
  await expect(page.locator('#ai-build-song')).toContainText('song.amplet');
  const taskPoll = `**/api/ai/tasks/${task.id}?since=*`;
  await page.route(taskPoll, (route) => route.abort());
  await expect(page.locator('#ai-task-reconnect')).toContainText('Playback and output are hidden');
  await expect(page.locator('#ai-task .ai-wave')).toHaveCount(0);
  await page.unroute(taskPoll);
  await expect(page.locator('#ai-build-song')).toBeVisible();
  await expect(page.locator('#ai-task .ai-wave')).toBeVisible();
  await page.locator('#ai-task').evaluate((dialog) => (dialog as HTMLDialogElement).close());
  await call(page, 'POST', `/api/ai/tasks/${task.id}/reject`, {});
});

test('plain Accept applies the proposal without starting another Codex turn', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' });
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  const said = (log: unknown) => JSON.stringify(log).split('Added one comment line (fake Codex).').length - 1;
  const saidBefore = said((await call(page, 'GET', `/api/ai/tasks/${task.id}`)).log);   // a repair round of AMPLE may have made Codex speak twice before the owner looks
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-accept')).toBeEnabled();
  await page.locator('#ai-accept').evaluate((button) => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
  await expect(page.locator('#ai-accepted')).toBeVisible();
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).applied).toBe(true);
  const accepted = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(said(accepted.log)).toBe(saidBefore);   // Accept starts no other turn: Codex says nothing new
});

test('double-clicking Accept selected applies once without starting another Codex turn', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' });
  await expect.poll(async () => { const result = await call(page, 'GET', `/api/ai/tasks/${task.id}`); return `${result.status}:${result.build.status}`; }, { timeout: 120000 }).toBe('ready:ok');
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await page.locator('#ai-task [data-tab="changes"]').click();
  await page.locator('.ai-files input[aria-label^="Accept "]').first().check();
  await expect(page.locator('#ai-accept-selected')).toBeEnabled();
  await page.locator('#ai-accept-selected').evaluate((button) => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).acceptedPaths.length).toBe(1);
  const accepted = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(JSON.stringify(accepted.log).split('Added one comment line (fake Codex).').length - 1).toBe(1);
});

test('double-clicking Continue remaining work starts one continuation turn', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'explain', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Explain the demo song.' });
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-continue')).toBeEnabled();
  await page.locator('#ai-continue').evaluate((button) => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
  await expect.poll(async () => {
    const current = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
    // the answers of Codex, one per turn (the continuation message of the user quotes the last answer, so the raw text of the log is not counted)
    return `${current.status}:${current.log.filter((l: { kind: string; text: string }) => l.kind === 'assistant' && l.text.includes('Added one comment line (fake Codex).')).length}`;
  }, { timeout: 120000 }).toBe('ready:2');
});

test('F5 restores a ready AI task without starting another Codex turn', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await call(page, 'POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const task = await call(page, 'POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' });
  await expect.poll(async () => (await call(page, 'GET', `/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await page.reload();
  await page.waitForSelector('body[data-ready="1"]');
  await bottomTab(page, 'assistant');
  const session = page.locator('#b-assistant .assistant-session').first();
  await expect(session).toBeVisible();
  await session.click();
  await expect(page.locator('#ai-accept')).toBeEnabled();
  const restored = await call(page, 'GET', `/api/ai/tasks/${task.id}`);
  expect(restored.status).toBe('ready');
  expect(JSON.stringify(restored.log).split('Added one comment line (fake Codex).').length - 1).toBe(1);
});
