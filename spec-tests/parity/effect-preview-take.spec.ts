import * as fs from 'node:fs';
import * as path from 'node:path';
import { test, expect } from '@playwright/test';
import { api, freshServer, openApp } from '../../web/tests/support';

// The live preview of an effect works on the take of the input that the page shows, also when nobody chose that take by hand.
function wav(samples: number[], rate = 44100): Buffer {
  const b = Buffer.alloc(44 + samples.length * 2);
  b.write('RIFF', 0); b.writeInt32LE(36 + samples.length * 2, 4); b.write('WAVEfmt ', 8); b.writeInt32LE(16, 16); b.writeInt16LE(1, 20); b.writeInt16LE(1, 22);
  b.writeInt32LE(rate, 24); b.writeInt32LE(rate * 2, 28); b.writeInt16LE(2, 32); b.writeInt16LE(16, 34); b.write('data', 36); b.writeInt32LE(samples.length * 2, 40);
  samples.forEach((s, i) => b.writeInt16LE(s, 44 + i * 2));
  return b;
}

freshServer({
  song: 'composition "fx" { ample 1  tempo 100  master-seed 3 }\nimport-audio src at 1 for 1 {\n  file samples/a.wav\n}\nvolume vol at 1 for 1 {\n  from src.audio -> audio\n}\n',
  prepare: (_cli, folder) => { const p = path.join(folder, 'samples', 'a.wav'); fs.mkdirSync(path.dirname(p), { recursive: true }); fs.writeFileSync(p, wav([...Array(44100)].map((_, i) => Math.round(12000 * Math.sin(i / 10))))); },
});

test('an effect preview accepts the built take of its input without a chosen take', async ({ page }) => {
  await api('/api/doc/save', { method: 'POST', body: { path: 'song.amplet' } });
  await openApp(page, { reset: false });
  const take = await page.evaluate(async () => { const store = await import('/app/store.js'); await store.build(); return store.S.build?.takes?.src || JSON.stringify([store.S.build?.status, store.S.buildErrors]).slice(0, 400); });
  expect(take).toMatch(/^sha256/);
  const result = await page.evaluate(async (takeId) => {
    const { api } = await import('/app/api.js');
    try { const blob = await api.effectPreview({ path: 'song.amplet', block: 'vol', takeId, fromMs: 0, toMs: 1000, params: { left: '-6', right: '-6' } }); return { ok: blob.size > 1000 }; } catch (e) { return { error: String((e as Error).message) }; }
  }, take);
  expect(result).toEqual({ ok: true });
});
