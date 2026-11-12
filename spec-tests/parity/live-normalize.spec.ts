import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 3.2: normalize sits in the live chain after a built sound: the gain is measured on the whole sound (peak or BS.1770 loudness) and the block is made again when a knob moves.
freshServer({ song: `${demoSong()}\nnormalize level {\n  mode loudness\n  loudness -18\n  from drums.audio -> audio\n}\n` });
test('normalize in the live chain says its gain and makes the sound louder to the target', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 4, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 1600), 44 + f * 2);   // about -26 dBFS: quiet
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('level'))).toBe(true);
  const state = await page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainState()));
  const item = state.items.find((i: any) => i.id === 'level');
  expect(item.kind).toBe('local');
  expect(item.info).toMatch(/^\+1\d\.\d dB$/);   // from about -29 LUFS to -18 LUFS
  await expect.poll(() => page.evaluate(() => import('/app/live-chain.js').then((m) => m.chainLevels('level')?.left ?? 0)), { timeout: 8000 }).toBeGreaterThan(0.1);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
