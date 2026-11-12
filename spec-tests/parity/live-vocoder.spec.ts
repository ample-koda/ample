import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 4.5 (re-render route): a vocoder with a voice and a carrier plays in the live chain without a build.
freshServer({ song: `${demoSong()}\nvocoder talk {\n  from drums.audio -> modulator\n  from bass.audio -> carrier\n}\n` });
test('vocoder plays in the live chain', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round((Math.sin(2 * Math.PI * 220 * f / rate) + Math.sin(2 * Math.PI * 1800 * f / rate)) * 5000), 44 + f * 2);
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' || key === 'bass' ? `take-${String(key)}` : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('talk'))).toBe(true);
  expect((await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()))).items.find((i: any) => i.id === 'talk').kind).toBe('local');
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('talk')?.left ?? 0)), { timeout: 30000 }).toBeGreaterThan(0.01);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
