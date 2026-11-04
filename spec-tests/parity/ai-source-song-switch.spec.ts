import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp, ownServer, state } from '../../web/tests/support';

freshServer({ files: { 'second.amplet': 'composition "second" { ample 1  tempo 111 master-seed 17 }\nmidi-clip other {\n  chords Dm\n  bars 2\n}\n' } });

test('opening another song during an AI work keeps its source title and text on the selected song', async ({ page }) => {
  test.setTimeout(180000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  await api('/api/ai/settings', { method: 'POST', body: { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true } });
  const task = await api('/api/ai/tasks', { method: 'POST', body: { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Add a comment. SLOW' } });
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('ample:open', { detail: 'second.amplet' })));
  await page.click('#vs-source');
  await expect(page.locator('#se-title')).toHaveText('second.amplet');
  await expect.poll(async () => (await state(page)).text).toContain('composition "second"');
  // #se-ta is the editor's own stand-in for a textarea: Playwright's toHaveValue/inputValue read the native element behind it, which stays empty, so read the stand-in's value
  await expect.poll(() => page.locator('#se-ta').evaluate((e) => (e as HTMLTextAreaElement).value)).toMatch(/composition "second"[\s\S]*midi-clip other/);
  await expect.poll(async () => (await api(`/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  expect((await state(page)).text).toContain('composition "second"');
  expect((await api('/api/doc?path=second.amplet')).text).toContain('midi-clip other');
  await api(`/api/ai/tasks/${task.id}/reject`, { method: 'POST', body: {} });
});
