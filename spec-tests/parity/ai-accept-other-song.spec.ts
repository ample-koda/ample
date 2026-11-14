import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, ownServer, state } from '../../web/tests/support';

freshServer({ files: { 'second.amplet': 'composition "second" { ample 1  tempo 111 master-seed 17 }\nmidi-clip other {\n  chords Dm\n  bars 2\n}\n' } });

test('accepting a proposal for another song leaves the open song editor untouched', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await api('/api/ai/settings', { method: 'POST', body: { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true } });
  const task = await api('/api/ai/tasks', { method: 'POST', body: { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' } });
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');

  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'second.amplet' })));
  await expect.poll(async () => page.evaluate(() => (window as any).AMPLE_STATE.path)).toBe('second.amplet');
  const otherText = (await state(page)).text;
  const songBefore = (await api('/api/doc?path=song.amplet')).text;
  await page.click('#vs-source');
  await expect(page.locator('#se-title')).toHaveText('second.amplet');

  await page.evaluate((id) => import('/app/ai.js').then((m) => m.openAiTask(id)), task.id);
  await expect(page.locator('#ai-accept')).toBeEnabled();
  await page.click('#ai-accept');
  await expect(page.locator('#ai-accepted')).toBeVisible();
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).applied).toBe(true);
  expect((await state(page)).text).toBe(otherText);
  expect((await api('/api/doc?path=second.amplet')).text).toBe(otherText);
  expect((await api(`/api/ai/tasks/${task.id}`)).applied).toBe(true);
  // the accepted song itself is changed on disk, not only reported as changed
  expect((await api('/api/doc?path=song.amplet')).text).not.toBe(songBefore);
});

test('Accept writes the song when another song is open, hands it to the editor when open, and Undo restores it', async () => {
  test.setTimeout(240000);
  const policy = { codexPath: path.resolve('web/tests/fixtures/fake-codex.js') };
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify(policy));
  await api('/api/ai/settings', { method: 'POST', body: { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true } });
  const original = (await api('/api/doc?path=song.amplet')).text;
  const propose = async () => {
    const body = { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment.' };
    const task = await api('/api/ai/tasks', { method: 'POST', body });
    await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
    return task.id as string;
  };

  const first = await propose();
  const written = await api(`/api/ai/tasks/${first}/accept`, { method: 'POST', body: { activeSong: 'second.amplet' } });
  expect(written.songWritten).toBe(true);
  expect(written.songText).toBeNull();
  expect((await api('/api/doc?path=song.amplet')).text).not.toBe(original);
  await api(`/api/ai/tasks/${first}/undo`, { method: 'POST' });
  expect((await api('/api/doc?path=song.amplet')).text).toBe(original);

  const second = await propose();
  const handed = await api(`/api/ai/tasks/${second}/accept`, { method: 'POST', body: { activeSong: 'song.amplet' } });
  expect(handed.songWritten).toBe(false);
  expect(typeof handed.songText).toBe('string');
  expect((await api('/api/doc?path=song.amplet')).text).toBe(original);   // the editor applies it, the server leaves the song alone
});
