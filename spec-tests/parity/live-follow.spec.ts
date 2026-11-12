import { test, expect } from '@playwright/test';
import { freshServer, openApp } from '../../web/tests/support';
import { demoSong } from '../../web/tests/server';

// Plan local-preview 2.8 (follow): the loudness of a built sound steers a parameter in the live chain while the loop plays.
freshServer({ song: `${demoSong()}\nfollow f {\n  baseline off\n  attack 20 ms\n  release 50 ms\n  from drums.audio -> in\n}\ndelay echo {\n  from drums.audio -> audio\n  from f.level -> mix\n}\n` });
test('a follow block moves the mix of a delay with the loudness of the sound', async ({ page }) => {
  await openApp(page, { reset: false });
  const rate = 44_100, frames = rate * 2, bytes = Buffer.alloc(44 + frames * 2);
  bytes.write('RIFF', 0); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVE', 8); bytes.write('fmt ', 12); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(rate, 24); bytes.writeUInt32LE(rate * 2, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(frames * 2, 40);
  for (let f = 0; f < frames; f++) bytes.writeInt16LE(f >= rate ? Math.round(Math.sin(2 * Math.PI * 440 * f / rate) * 9000) : 0, 44 + f * 2);   // silent first second, loud second
  await page.route('**/api/takes/**/download*', (route) => route.fulfill({ status: 200, contentType: 'audio/wav', body: bytes }));
  await page.evaluate(() => import('/app/store.js').then((s) => { Object.defineProperty(s.S.build, 'takes', { get: () => new Proxy({}, { get: (_, key) => (key === 'drums' ? 'take-drums' : undefined) }), configurable: true }); }));
  expect(await page.evaluate(async () => (await import('/app/live-chain.js')).startChain('echo'))).toBe(true);
  const seen = new Set<number>();
  for (let i = 0; i < 40; i++) { const mix = await page.evaluate(() => import('/app/live-chain.js').then((m) => (m as any).controlledParams?.('echo')?.mix ?? null)); if (mix) seen.add(parseInt(mix)); await page.waitForTimeout(100); }
  expect(Math.max(...seen) - Math.min(...seen), 'the mix followed the loudness: ' + [...seen].join(',')).toBeGreaterThan(40);
  await page.evaluate(() => import('/app/live-chain.js').then((m) => m.stopChain()));
});
