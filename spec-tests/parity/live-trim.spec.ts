import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.5 (trim, silence): both are made in the browser in the live chain.
freshServer({ song: `${demoSong()}\ntrim tight {\n  from drums.audio -> audio\n}\nsilence gap {\n  length 500 ms\n}\njoin both {\n  from gap.audio -> audio1\n  from tight.audio -> audio2\n}\n` });
test('trim and silence play in the live chain, trim reports what it cut', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 3, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(f > rate && f < 2 * rate ? Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000) : 0, 44 + f * 2);
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
  const ok = await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('both'));
  expect(ok).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  expect(state.items.find((i: any) => i.id === 'tight').info).toMatch(/sound from 1\.0\d s to 2\.0\d s/);
  expect(state.items.find((i: any) => i.id === 'gap').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('both')?.left ?? 0)), { timeout: 15000 }).toBeGreaterThan(0.02);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
