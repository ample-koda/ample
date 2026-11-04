import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

// Audio the user offers to Codex: it is only offered, and the user decides at Accept which of it comes along (stand-in Codex).
freshServer();

const makeWav = (hz: number) => {
  const rate = 44100, n = rate / 2, bytes = new Uint8Array(44 + n * 2), view = new DataView(bytes.buffer);
  const ascii = (at: number, t: string) => { for (let i = 0; i < t.length; i++) bytes[at + i] = t.charCodeAt(i); };
  ascii(0, 'RIFF'); view.setUint32(4, 36 + n * 2, true); ascii(8, 'WAVE'); ascii(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true); ascii(36, 'data'); view.setUint32(40, n * 2, true);
  for (let i = 0; i < n; i++) view.setInt16(44 + i * 2, Math.sin((2 * Math.PI * hz * i) / rate) * 0.4 * 32767, true);
  return Array.from(bytes);
};

test('offered audio is used by Codex from a path of its own, and only the files the user chooses come along when the proposal is accepted', async ({ page }) => {
  test.setTimeout(300000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token') || '');
  const api = (method: string, url: string, body?: unknown, binary?: number[]) => page.evaluate(async ([m, u, b, bin, t]) => {
    const r = await fetch(u as string, { method: m as string, headers: { 'X-Ample-Token': t as string, 'Content-Type': bin ? 'application/octet-stream' : 'application/json' }, body: bin ? new Uint8Array(bin as number[]) : b ? JSON.stringify(b) : undefined });
    return r.json();
  }, [method, url, body ?? null, binary ?? null, token]);
  await api('POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const kick = await api('POST', '/api/ai/attachment?name=my%20kick.wav', undefined, makeWav(60));
  const spare = await api('POST', '/api/ai/attachment?name=spare.wav', undefined, makeWav(880));
  expect(kick.name).toBe('my_kick.wav');
  expect(kick.size).toBeGreaterThan(40000);
  expect(kick.pitch).toBeTruthy();

  const task = await api('POST', '/api/ai/tasks', { kind: 'custom', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Use a sound. USE_OFFERED', offered: [kick.id, spare.id] });
  await expect.poll(async () => { const t = await api('GET', `/api/ai/tasks/${task.id}`); return t.status === 'ready' && ['ok', 'failed'].includes(t.build.status) ? t.build.status : ''; }, { timeout: 240000 }).toBe('ok');
  const ready = await api('GET', `/api/ai/tasks/${task.id}`);
  expect(ready.offered.map((o: { name: string; used: boolean }) => [o.name, o.used])).toEqual([['my_kick.wav', true], ['spare.wav', false]]);
  expect(JSON.stringify(ready.changes[0].diff)).toContain('samples/offered/my_kick.wav');

  // the used one has to come along: accepting without it is refused and nothing changes
  const refused = await api('POST', `/api/ai/tasks/${task.id}/accept`, { include: [] });
  expect(JSON.stringify(refused)).toContain('AMP5463');
  expect(fs.existsSync(path.join(ownServer()!.folder, 'samples', 'offered'))).toBe(false);
  // taking only the used one along: it is copied into the project, the spare one is not
  const accepted = await api('POST', `/api/ai/tasks/${task.id}/accept`, { include: [kick.id] });
  expect(accepted.song).toBe('song.amplet');
  expect(fs.existsSync(path.join(ownServer()!.folder, 'samples', 'offered', 'my_kick.wav'))).toBe(true);
  expect(fs.existsSync(path.join(ownServer()!.folder, 'samples', 'offered', 'spare.wav'))).toBe(false);
  // undo takes it away again
  await api('POST', `/api/ai/tasks/${task.id}/undo`, {});
  expect(fs.existsSync(path.join(ownServer()!.folder, 'samples', 'offered', 'my_kick.wav'))).toBe(false);
});

test('a sound can be picked in the AI window and shows as something that is only offered', async ({ page }) => {
  await openApp(page);
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token') || '');
  await page.evaluate((t) => fetch('/api/ai/settings', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Ample-Token': t }, body: JSON.stringify({ assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true }) }), token);
  await page.reload(); await page.waitForSelector('body[data-ready="1"]');
  await page.click('#vs-source'); await page.locator('#se-cm .cm-content').click(); await page.locator('#se-ai').click();
  await page.locator('#menu div', { hasText: 'Generate' }).first().click();
  await expect(page.locator('#offer-add')).toBeVisible();
  await expect(page.locator('#offer-record')).toBeVisible();
  await page.setInputFiles('#offer-file', { name: 'snare drum.wav', mimeType: 'audio/wav', buffer: Buffer.from(makeWav(200)) });
  await expect(page.locator('#offer-chips .hum-chip')).toContainText('snare_drum.wav');
  await expect(page.locator('#offer-chips .hum-chip')).toHaveAttribute('title', /only if you choose|comes into the project only/);
});
