import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { freshServer, openApp, ownServer } from '../../web/tests/support';

// "Hum a melody": the notes AMPLE finds in a hummed line, the review dialog, and handing the recording to Codex (with a stand-in Codex).
freshServer();

// A4, C5, E5 and G4, half a second each (one beat at 120 BPM), made in the page
const makeWav = () => {
  const rate = 44100, parts: [number, number][] = [[440, 0.5], [523.25, 0.5], [659.26, 0.5], [0, 0.25], [392, 0.5]];
  const samples: number[] = []; let phase = 0;
  for (const [hz, seconds] of parts) { const n = Math.floor(seconds * rate); for (let i = 0; i < n; i++) { phase += (2 * Math.PI * hz) / rate; const fade = Math.min(1, Math.min(i, n - i) / (0.02 * rate)); samples.push(hz === 0 ? 0 : fade * 0.4 * (Math.sin(phase) + 0.4 * Math.sin(2 * phase))); } }
  const bytes = new Uint8Array(44 + samples.length * 2), view = new DataView(bytes.buffer);
  const ascii = (at: number, t: string) => { for (let i = 0; i < t.length; i++) bytes[at + i] = t.charCodeAt(i); };
  ascii(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); ascii(8, 'WAVE'); ascii(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, rate, true); view.setUint32(28, rate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true); ascii(36, 'data'); view.setUint32(40, samples.length * 2, true);
  samples.forEach((v, i) => view.setInt16(44 + i * 2, Math.max(-1, Math.min(1, v)) * 32767, true));
  return Array.from(bytes);
};

test('the notes in a hummed line are found, shown, and handed on only after the user approves them', async ({ page }) => {
  await openApp(page);
  const wav = await page.evaluate(makeWav);
  const found = await page.evaluate(async ([bytes]) => {
    const m = await import('/app/hum.js');
    return m.transcribe(new Uint8Array(bytes as number[]), { tempo: 120, grid: 0.25, key: '', bar: 1 });
  }, [wav]);
  expect(found.notes.map((n: { name: string }) => n.name)).toEqual(['A4', 'C5', 'E5', 'G4']);
  expect(found.notesText).toMatch(/^1:0:1:69:/);

  let accepted = false;
  await page.exposeFunction('humAccepted', () => { accepted = true; });
  await page.evaluate(async ([bytes]) => {
    const m = await import('/app/hum.js');
    await m.reviewHum(new Uint8Array(bytes as number[]), { tempo: 120, bar: 1, acceptLabel: 'Use this melody', onAccept: async () => { (window as unknown as { humAccepted: () => void }).humAccepted(); } });
  }, [wav]);
  await expect(page.locator('#hum-info')).toContainText('4 note(s)', { timeout: 20000 });
  await expect(page.locator('#hum-notes .hum-note')).toHaveText(['A4', 'C5', 'E5', 'G4']);
  await page.locator('#hum-review select[aria-label="Key"]').selectOption('A minor');
  await expect(page.locator('#hum-info')).toContainText('4 note(s)');
  expect(accepted).toBe(false);   // nothing is handed on before the user approves
  await page.click('#hum-accept');
  await expect.poll(() => accepted).toBe(true);
});

test('an approved hum goes to Codex as raw audio in its working folder, with the tool that turns it into notes', async ({ page }) => {
  test.setTimeout(240000);
  fs.writeFileSync(path.join(ownServer()!.home, 'ai-policy.json'), JSON.stringify({ codexPath: path.resolve('web/tests/fixtures/fake-codex.js') }));
  await openApp(page);
  const token = await page.evaluate(() => sessionStorage.getItem('ample.token') || '');
  const wav = await page.evaluate(makeWav);
  const api = (method: string, url: string, body?: unknown, binary?: number[]) => page.evaluate(async ([m, u, b, bin, t]) => {
    const r = await fetch(u as string, { method: m as string, headers: { 'X-Ample-Token': t as string, 'Content-Type': bin ? 'application/octet-stream' : 'application/json' }, body: bin ? new Uint8Array(bin as number[]) : b ? JSON.stringify(b) : undefined });
    return r.json();
  }, [method, url, body ?? null, binary ?? null, token]);
  await api('POST', '/api/ai/settings', { assistant: 'codex-server', mode: 'manual', acknowledgeRisk: true });
  const kept = await api('POST', '/api/ai/hum?tempo=120&grid=0.25', undefined, wav);
  expect(kept.id).toHaveLength(12);
  expect(kept.noteCount).toBe(4);
  const task = await api('POST', '/api/ai/tasks', { kind: 'comment', scope: 'file', path: 'song.amplet', song: 'song.amplet', prompt: 'Use my hum.', hums: [kept.id] });
  await expect.poll(async () => (await api('GET', `/api/ai/tasks/${task.id}`)).status, { timeout: 120000 }).toBe('ready');
  const final = await api('GET', `/api/ai/tasks/${task.id}`);
  expect(JSON.stringify(final.log)).toContain('attached 1 hummed melody');
  expect(fs.existsSync(path.join(ownServer()!.folder, '.ample', 'ai', 'work', task.id, 'assets', 'hum', `${kept.id}.wav`))).toBe(true);
  await api('POST', `/api/ai/tasks/${task.id}/reject`, {});
});
